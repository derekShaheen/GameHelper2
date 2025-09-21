// <copyright file="PManager.cs" company="None">
// Copyright (c) None. All rights reserved.
// </copyright>

namespace GameHelper.Plugin
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Reflection;
    using System.Threading.Tasks;
    using System.IO.Compression; // added for zip extraction
    using Coroutine;
    using CoroutineEvents;
    using CTOUtils = ClickableTransparentOverlay.Win32.Utils;
    using Settings;
    using Ui;
    using Utils;

    internal record PluginWithName(string Name, IPCore Plugin);

    internal record PluginContainer(string Name, IPCore Plugin, PluginMetadata Metadata);

    /// <summary>
    ///     Finds, loads and unloads the plugins.
    /// </summary>
    internal static class PManager
    {
        private static bool disableRendering = false;
#if DEBUG
        internal static readonly List<string> PluginNames = new();
#endif
        internal static readonly List<PluginContainer> Plugins = new();

        /// <summary>
        ///     Initlizes the plugin manager by loading all the plugins and their Metadata.
        /// </summary>
        internal static void InitializePlugins()
        {
            State.PluginsDirectory.Create(); // doesn't do anything if already exists.

            // Expand any .zip files dropped in the Plugins folder before scanning directories.
            ExtractZipPluginsIfAny();

            LoadPluginMetadata(LoadPlugins());
#if DEBUG
            GetAllPluginNames();
#endif
            Parallel.ForEach(Plugins, EnablePluginIfRequired);
            CoroutineHandler.Start(SavePluginSettingsCoroutine());
            CoroutineHandler.Start(SavePluginMetadataCoroutine());
            Core.CoroutinesRegistrar.Add(CoroutineHandler.Start(
                DrawPluginUiRenderCoroutine(), "[PManager] Draw Plugins UI"));
        }

        /// <summary>
        /// Extract any non-hidden *.zip files in the Plugins directory.
        /// Destination folder name is taken from the plugin DLL file name (without extension).
        /// If the ZIP already contains a single top-level folder, we avoid nesting by moving that folder.
        /// Ensures the DLL exists at the destination folder root so ReadPluginFiles can find it.
        /// Deletes the ZIP after a successful extraction.
        /// </summary>
        private static void ExtractZipPluginsIfAny()
        {
            try
            {
                var zips = State.PluginsDirectory
                    .GetFiles("*.zip", SearchOption.TopDirectoryOnly)
                    .Where(f => (f.Attributes & FileAttributes.Hidden) == 0)
                    .ToList();

                foreach (var zip in zips)
                {
                    string tempDir = null;
                    try
                    {
                        tempDir = Path.Combine(State.PluginsDirectory.FullName, Guid.NewGuid().ToString("N"));
                        Directory.CreateDirectory(tempDir);

                        // Extract into a temporary folder
                        ZipFile.ExtractToDirectory(zip.FullName, tempDir, overwriteFiles: true);

                        // Detect if archive has a single root directory (no files at temp root, exactly one directory)
                        var topFiles = Directory.GetFiles(tempDir, "*", SearchOption.TopDirectoryOnly);
                        var topDirs = Directory.GetDirectories(tempDir, "*", SearchOption.TopDirectoryOnly);

                        // If there is exactly one top-level folder and no top-level files, use that as content root
                        var contentRoot = (topFiles.Length == 0 && topDirs.Length == 1) ? topDirs[0] : tempDir;

                        // Find the plugin dll anywhere under the content root
                        var dllPath = Directory
                            .EnumerateFiles(contentRoot, "*.dll", SearchOption.AllDirectories)
                            .FirstOrDefault();

                        if (dllPath == null)
                        {
                            Console.WriteLine($"[PManager] No dll found in '{zip.Name}', skipping.");
                            Directory.Delete(tempDir, true);
                            continue;
                        }

                        // Ensure the DLL is present at the root of the contentRoot (so after move, it is at finalDir root).
                        // If it's nested, copy it up to the contentRoot.
                        var dllRel = Path.GetRelativePath(contentRoot, dllPath);
                        var dllRelDir = Path.GetDirectoryName(dllRel);
                        if (!string.IsNullOrEmpty(dllRelDir))
                        {
                            var dllAtRoot = Path.Combine(contentRoot, Path.GetFileName(dllPath));
                            try
                            {
                                File.Copy(dllPath, dllAtRoot, overwrite: true);
                                dllPath = dllAtRoot; // update reference to the top-level copy
                            }
                            catch (Exception copyEx)
                            {
                                Console.WriteLine($"[PManager] Failed to copy DLL to root for '{zip.Name}': {copyEx}");
                            }
                        }

                        // Final folder name comes from the dll's file name (minus extension)
                        var dllBaseName = Path.GetFileNameWithoutExtension(dllPath);
                        var finalDir = Path.Combine(State.PluginsDirectory.FullName, dllBaseName);

                        // If the destination already exists, remove it to avoid nesting/merge issues
                        if (Directory.Exists(finalDir))
                        {
                            try
                            {
                                Directory.Delete(finalDir, true);
                            }
                            catch (Exception delEx)
                            {
                                Console.WriteLine($"[PManager] Failed clearing existing folder '{finalDir}': {delEx}");
                                // If we can't clear, skip to next zip to avoid undefined state
                                Directory.Delete(tempDir, true);
                                continue;
                            }
                        }

                        // Move the content root into place:
                        //   - If contentRoot == tempDir, this is the whole extracted set (no single root folder)
                        //   - If contentRoot is the single root folder, move just that folder (prevents extra nesting)
                        if (string.Equals(contentRoot, tempDir, StringComparison.OrdinalIgnoreCase))
                        {
                            Directory.Move(tempDir, finalDir);
                        }
                        else
                        {
                            Directory.Move(contentRoot, finalDir);
                            // Clean up the now-empty tempDir that still exists alongside
                            if (Directory.Exists(tempDir))
                            {
                                Directory.Delete(tempDir, true);
                            }
                        }

                        // Remove the zip after successful extraction/placement
                        zip.Delete();

                        Console.WriteLine($"[PManager] Extracted '{zip.Name}' to '{finalDir}' and removed the archive.");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[PManager] Failed to extract '{zip.FullName}': {ex}");
                        // Best-effort cleanup
                        try
                        {
                            if (!string.IsNullOrWhiteSpace(tempDir) && Directory.Exists(tempDir))
                                Directory.Delete(tempDir, true);
                        }
                        catch
                        {
                            // ignore cleanup errors
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[PManager] ZIP scan failed: {ex}");
            }
        }

        private static List<PluginWithName> LoadPlugins()
        {
            return GetPluginsDirectories()
                  .AsParallel()
                  .Select(LoadPlugin)
                  .Where(x => x != null)
                  .OrderBy(x => x.Name)
                  .ToList();
        }

#if DEBUG
        private static void GetAllPluginNames()
        {
            foreach (var plugin in Plugins)
            {
                PluginNames.Add(plugin.Name);
            }
        }

        /// <summary>
        ///     Cleans up the already loaded plugins.
        /// </summary>
        internal static bool UnloadPlugin(string name)
        {
            foreach (var plugin in Plugins)
            {
                if (plugin.Name == name)
                {
                    plugin.Plugin.SaveSettings();
                    plugin.Plugin.OnDisable();
                    Plugins.Remove(plugin);
                    return true;
                }
            }

            return false;
        }

        internal static bool LoadPlugin(string name)
        {
            try
            {
                var container = GetPluginsDirectories()
                                .Where(x => x.Name.Contains(name))
                                .Select(LoadPlugin)
                                .Where(y => y != null)
                                .ToList();
                if (container.Count > 0)
                {
                    LoadPluginMetadata(container);
                    container[0].Plugin.OnEnable(Core.Process.Address != IntPtr.Zero);
                    return true;
                }
                else
                {
                    return false;
                }
            }
            catch
            {
                return false;
            }
        }
#endif

        private static List<DirectoryInfo> GetPluginsDirectories()
        {
            return State.PluginsDirectory.GetDirectories().Where(
                x => (x.Attributes & FileAttributes.Hidden) == 0).ToList();
        }

        private static Assembly ReadPluginFiles(DirectoryInfo pluginDirectory)
        {
            try
            {
                var dllFile = pluginDirectory.GetFiles(
                    $"{pluginDirectory.Name}*.dll",
                    SearchOption.TopDirectoryOnly).FirstOrDefault();
                if (dllFile == null)
                {
                    Console.WriteLine($"Couldn't find plugin dll with name {pluginDirectory.Name}" +
                                      $" in directory {pluginDirectory.FullName}." +
                                      " Please make sure DLL & the plugin got same name.");
                    return null;
                }

                return new PluginAssemblyLoadContext(dllFile.FullName)
                   .LoadFromAssemblyPath(dllFile.FullName);
            }
            catch (Exception e)
            {
                Console.WriteLine($"Failed to load plugin {pluginDirectory.FullName} due to {e}");
                return null;
            }
        }

        private static PluginWithName LoadPlugin(DirectoryInfo pluginDirectory)
        {
            var assembly = ReadPluginFiles(pluginDirectory);
            if (assembly != null)
            {
                var relativePluginDir = pluginDirectory.FullName.Replace(
                    State.PluginsDirectory.FullName, State.PluginsDirectory.Name);
                return LoadPlugin(assembly, relativePluginDir);
            }

            return null;
        }

        private static PluginWithName LoadPlugin(Assembly assembly, string pluginRootDirectory)
        {
            try
            {
                var types = assembly.GetTypes();
                if (types.Length <= 0)
                {
                    Console.WriteLine($"Plugin (in {pluginRootDirectory}) {assembly} doesn't " +
                                      "contain any types (i.e. classes/stuctures).");
                    return null;
                }

                var iPluginClasses = types.Where(
                    type => typeof(IPCore).IsAssignableFrom(type) &&
                            type.IsSealed).ToList();
                if (iPluginClasses.Count != 1)
                {
                    Console.WriteLine($"Plugin (in {pluginRootDirectory}) {assembly} contains" +
                                      $" {iPluginClasses.Count} sealed classes derived from CoreBase<TSettings>." +
                                      " It should have one sealed class derived from IPlugin.");
                    return null;
                }

                var pluginCore = Activator.CreateInstance(iPluginClasses[0]) as IPCore;
                pluginCore.SetPluginDllLocation(pluginRootDirectory);
                return new PluginWithName(assembly.GetName().Name, pluginCore);
            }
            catch (Exception e)
            {
                Console.WriteLine($"Error loading plugin {assembly.FullName} due to {e}");
                return null;
            }
        }

        private static void LoadPluginMetadata(IEnumerable<PluginWithName> plugins)
        {
            var metadata = JsonHelper.CreateOrLoadJsonFile<Dictionary<string, PluginMetadata>>(State.PluginsMetadataFile);
            Plugins.AddRange(
                plugins.Select(
                    x => new PluginContainer(
                        x.Name,
                        x.Plugin,
                        metadata.GetValueOrDefault(
                            x.Name,
                            new PluginMetadata()))));

            SavePluginMetadata();
        }

        private static void EnablePluginIfRequired(PluginContainer container)
        {
            if (container.Metadata.Enable)
            {
                container.Plugin.OnEnable(Core.Process.Address != IntPtr.Zero);
            }
        }

        private static void SavePluginMetadata()
        {
            JsonHelper.SafeToFile(Plugins.ToDictionary(x => x.Name, x => x.Metadata), State.PluginsMetadataFile);
        }

        private static IEnumerator<Wait> SavePluginMetadataCoroutine()
        {
            while (true)
            {
                yield return new Wait(GameHelperEvents.TimeToSaveAllSettings);
                SavePluginMetadata();
            }
        }

        private static IEnumerator<Wait> SavePluginSettingsCoroutine()
        {
            while (true)
            {
                yield return new Wait(GameHelperEvents.TimeToSaveAllSettings);
                foreach (var container in Plugins)
                {
                    container.Plugin.SaveSettings();
                }
            }
        }

        private static IEnumerator<Wait> DrawPluginUiRenderCoroutine()
        {
            while (true)
            {
                yield return new Wait(GameHelperEvents.OnRender);
                if (CTOUtils.IsKeyPressedAndNotTimeout(Core.GHSettings.DisableAllRenderingKey))
                {
                    disableRendering = !disableRendering;
                }

                if (disableRendering)
                {
                    continue;
                }

                foreach (var container in Plugins)
                {
                    if (container.Metadata.Enable)
                    {
                        using var _ = PerformanceProfiler.Profile(container.Plugin.GetType().FullName, "DrawUI");
                        container.Plugin.DrawUI();
                    }
                }
            }
        }
    }
}
