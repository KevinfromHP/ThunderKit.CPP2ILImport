using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using ThunderKit.Core.Config;
using ThunderKit.Core.Data;
using ThunderKit.Markdown;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine.UIElements;
using Debug = UnityEngine.Debug;
using Object = UnityEngine.Object;
using ThunderKit.CPP2ILImport.Common;
using System;
using ThunderKit.Core.Utilities;

namespace ThunderKit.CPP2ILImport.Config
{
    public class CPP2ILImporter : OptionalExecutor
    {
        public override int Priority => ThunderKit.Common.Constants.Priority.AssemblyImport + 201;
        public override string Name => "CPP2IL Importer";
        public override string Description => "Use if the game's assembly is built with IL2CPP instead of Mono. Cannot have other AssemblyImporters enabled.";

        public Object cpp2ilExe;

        public bool attemptILToDLL;
        public bool parallel;
        public bool throwSafetyOutWindow;
        public bool suppressAttributes = true;
        public bool disableAutoSetup;

        private SerializedObject serializedObject;
        private VisualElement rootVisualElement;
        private MarkdownElement MessageElement
        {
            get
            {
                if (_messageElement == null)
                    _messageElement = new MarkdownElement() { MarkdownDataType = MarkdownDataType.Text };
                return _messageElement;
            }
        }
        private MarkdownElement _messageElement;

        [InitializeOnLoadMethod]
        public static void DisableImportAssemblies()
        {
            var settings = ThunderKitSetting.GetOrCreateSettings<ThunderKitSettings>();
            var executors = ThunderKitSetting.GetOrCreateSettings<ImportConfiguration>().ConfigurationExecutors;

            var importer = executors.OfType<CPP2ILImporter>().FirstOrDefault();
            if (!importer || !importer.enabled || importer.disableAutoSetup || string.IsNullOrWhiteSpace(settings.GamePath))
                return;
            if (!Directory.Exists(Path.Combine(settings.GameDataPath, "il2cpp_data")))
            {
                Debug.LogWarning("Game IL2CPP data structure not found. Disabling CPP2ILImporter in ThunderKit Import Configuration.");
                importer.enabled = false;
                return;
            }
            var assemblyImporter = executors.OfType<ImportAssemblies>().FirstOrDefault();
            if (assemblyImporter && assemblyImporter.enabled)
            {
                assemblyImporter.enabled = false;
                Debug.Log("CPP2IL has automatically disabled ImportAssemblies in ThunderKit Import Configuration. This can be turned off under the CPP2IL Import Configuration or by disabling the CPP2IL Importer.");
            }
        }

        public override bool Execute()
        {
            var settings = ThunderKitSetting.GetOrCreateSettings<ThunderKitSettings>();
            var packageName = Path.GetFileNameWithoutExtension(settings.GameExecutable);

            AssertDestinations(packageName);
            if (!cpp2ilExe)
            {
                Debug.LogError("Unable to find CPP2IL.exe! Check the import config to make sure it exists!");
                return false;
            }
            try
            {
                AssetDatabase.StartAssetEditing();
                EditorApplication.LockReloadAssemblies();

                var blackList = BuildAssemblyBlacklist();
                var whitelist = BuildBinaryWhitelist(settings);

                var packagePath = Path.Combine("Packages", packageName);



                if (!File.Exists(Path.Combine(settings.GamePath, $"GameAssembly.dll")))
                {
                    Debug.LogError("GameAssembly.dll not found!");
                    return false;
                }

                var asmPaths = RunCpp2IL();
                if (asmPaths.Count() == 0)
                {
                    Debug.LogWarning("Files from output of CPP2IL not found!");
                    return false;
                }
                ImportFilteredAssemblies(packagePath, asmPaths, blackList, whitelist);


                //Note: these will still be in C++
                var pluginsPath = Path.Combine(settings.GameDataPath, "Plugins");
                if (Directory.Exists(pluginsPath))
                {
                    var packagePluginsPath = Path.Combine(packagePath, "plugins");
                    var plugins = Directory.EnumerateFiles(pluginsPath, $"*", SearchOption.AllDirectories);
                    ImportFilteredAssemblies(packagePluginsPath, plugins, blackList, whitelist);
                }
            }
            finally
            {
                EditorApplication.UnlockReloadAssemblies();
                AssetDatabase.StopAssetEditing();
            }
            return true;
        }


        #region CPP2IL
        private bool TryFindCPP2ILExecutable(out Object cpp2ilExecutable)
        {
            cpp2ilExecutable = AssetDatabase.LoadAssetAtPath<Object>(Constants.CPP2ILExePath);
            if (!cpp2ilExecutable)
            {
                var cpp2ILPath = AssetDatabase.FindAssets("")
                                              .Select(guid => AssetDatabase.GUIDToAssetPath(guid))
                                              .FirstOrDefault(path => path.EndsWith("Cpp2IL.exe"));

                cpp2ilExecutable = AssetDatabase.LoadAssetAtPath<Object>(cpp2ILPath);
            }

            return cpp2ilExecutable;
        }
        private void OnCPP2ILSet(SerializedPropertyChangeEvent evt)
        {
            var cpp2il = evt.changedProperty.objectReferenceValue;
            if (cpp2il == null)
            {
                MessageElement.Data = $"***__WARNING__***: Could not find Cpp2IL Executable!.";
                if (!rootVisualElement.Contains(MessageElement))
                {
                    rootVisualElement.Add(MessageElement);
                }
                return;
            }
            var relativePath = AssetDatabase.GetAssetPath(cpp2il);
            var fullPath = Path.GetFullPath(relativePath);
            var fileName = Path.GetFileName(fullPath);


            if (fileName != "Cpp2IL.exe")
            {
                MessageElement.Data = $"Object in \"Cpp2IL.exe\" is not Cpp2IL!";
                if (!rootVisualElement.Contains(MessageElement))
                {
                    rootVisualElement.Add(MessageElement);
                }
                return;
            }

            evt.changedProperty.serializedObject.ApplyModifiedProperties();
            MessageElement.RemoveFromHierarchy();
        }
        private IEnumerable<string> RunCpp2IL()
        {
            var settings = ThunderKitSetting.GetOrCreateSettings<ThunderKitSettings>();
            Directory.CreateDirectory(Constants.CPP2ILTempDir);
            var cpp2ilExePath = Path.GetFullPath(AssetDatabase.GetAssetPath(cpp2ilExe));

            var args = new List<string>()
            {
                "--skip-analysis",
                "--analyze-all",
                (attemptILToDLL ? "--experimental-enable-il-to-assembly-please" : ""),
                (parallel ? "--parallel" : ""),
                (throwSafetyOutWindow ? "--throw-safety-out-the-window" : ""),
                (suppressAttributes ? "--suppress-attributes" : ""),
                "--skip-method-dumps",
                "--skip-metadata-txts",

                "--output-root",
                $"\"{Constants.CPP2ILTempDir}\"",
                $"--exe-name",
                $"\"{Path.GetFileNameWithoutExtension(settings.GameExecutable)}\"",
                $"--game-path",
                $"\"{settings.GamePath}\"",
            }.Where(s => !string.IsNullOrEmpty(s)).Aggregate("", (cur, nex) => cur + " " + nex);
            Debug.Log($"Executing {cpp2ilExePath} with the following arguments:\n{args}");

            //Process.GetProcessesByName(cpp2ilExePath);
            var processStartInfo = new ProcessStartInfo(cpp2ilExePath, args) { WorkingDirectory = Directory.GetCurrentDirectory() };
            var process = Process.Start(processStartInfo);
            process.WaitForExit(3000);

            var filePaths = Directory.GetFiles(Constants.CPP2ILTempDir, "*.dll", SearchOption.TopDirectoryOnly).Distinct();

            var debugFiles = filePaths.Select(Path.GetFileName).Aggregate("\n\t", (cur, nex) => cur + "\n\t" + nex);
            Debug.Log("CPP2IL.exe Converted the following assemblies:" + debugFiles);

            return filePaths;
        }
        #endregion


        #region Copied from ImportAssemblies
        private static void ImportFilteredAssemblies(string destinationFolder, IEnumerable<string> assemblies, HashSet<string> blackList, HashSet<string> whitelist)
        {
            foreach (var assemblyPath in assemblies)
            {
                var asmPath = assemblyPath.Replace("\\", "/");
                foreach (var processor in ImportAssemblies.AssemblyProcessors)
                    asmPath = processor.Process(asmPath);

                string assemblyFileName = Path.GetFileName(asmPath);
                if (!whitelist.Contains(assemblyFileName)
                  && blackList.Contains(assemblyFileName))
                    continue;

                var destinationFile = Path.Combine(destinationFolder, assemblyFileName);

                var destinationMetaData = Path.Combine(destinationFolder, $"{assemblyFileName}.meta");

                try
                {
                    if (File.Exists(destinationFile)) File.Delete(destinationFile);
                    File.Copy(asmPath, destinationFile);

                    PackageHelper.WriteAssemblyMetaData(asmPath, destinationMetaData);
                }
                catch
                {
                    Debug.LogWarning($"Could not update assembly: {destinationFile}", AssetDatabase.LoadAssetAtPath<Object>(destinationFile));
                }
            }
        }

        private static HashSet<string> BuildBinaryWhitelist(ThunderKitSettings settings)
        {
            string[] installedGameAssemblies = Array.Empty<string>();
            if (Directory.Exists(settings.PackagePath))
                installedGameAssemblies = Directory.EnumerateFiles(settings.PackagePath, "*", SearchOption.AllDirectories)
                                       .Select(path => Path.GetFileName(path))
                                       .Distinct()
                                       .ToArray();

            var whitelist = new HashSet<string>(installedGameAssemblies);

            var enumerable = whitelist as IEnumerable<string>;

            foreach (var processor in ImportAssemblies.WhitelistProcessors)
                enumerable = processor.Process(enumerable);
            return whitelist;
        }
        /// <summary>
        /// Collect list of Assemblies that should not be imported from the game.
        /// These are assemblies that would be automatically provided by Unity to the environment
        /// </summary>
        /// <param name="byEditorFiles"></param>
        private static HashSet<string> BuildAssemblyBlacklist(bool byEditorFiles = false)
        {
            var result = new HashSet<string>();
            if (byEditorFiles)
            {
                var editorPath = Path.GetDirectoryName(EditorApplication.applicationPath);
                var extensionsFolder = Path.Combine(editorPath, "Data", "Managed");
                foreach (var asmFile in Directory.GetFiles(extensionsFolder, "*.dll", SearchOption.AllDirectories))
                {
                    result.Add(Path.GetFileName(asmFile));
                }
            }
            else
            {
                var blackList = AppDomain.CurrentDomain.GetAssemblies()
#if NET_4_6
                .Where(asm => !asm.IsDynamic)
#else
                .Where(asm =>
                {
                    if (asm.ManifestModule is System.Reflection.Emit.ModuleBuilder mb)
                        return !mb.IsTransient();

                    return true;
                })
#endif
                .Select(asm => asm.Location)
                    .Select(location =>
                    {
                        try
                        {
                            return Path.GetFileName(location);
                        }
                        catch
                        {
                            return string.Empty;
                        }
                    })
                    .OrderBy(s => s);
                foreach (var asm in blackList)
                    result.Add(asm);
            }

            var enumerable = result as IEnumerable<string>;

            foreach (var processor in ImportAssemblies.BlacklistProcessors)
                enumerable = processor.Process(enumerable);

            return new HashSet<string>(enumerable);
        }

        private static void AssertDestinations(string packageName)
        {
            var destinationFolder = Path.Combine("Packages", packageName);
            if (!Directory.Exists(destinationFolder))
                Directory.CreateDirectory(destinationFolder);

            destinationFolder = Path.Combine("Packages", packageName, "plugins");
            if (!Directory.Exists(destinationFolder))
                Directory.CreateDirectory(destinationFolder);
        }
        #endregion

        protected override VisualElement CreateProperties()
        {
            var settings = ThunderKitSetting.GetOrCreateSettings<ThunderKitSettings>();

            rootVisualElement = new VisualElement();
            serializedObject = new SerializedObject(this);
            var pCpp2ilExe = serializedObject.FindProperty(nameof(cpp2ilExe));
            var pAttemptILToDLL = serializedObject.FindProperty(nameof(attemptILToDLL));
            var pParallel = serializedObject.FindProperty(nameof(parallel));
            var pThrowSafetyOutWindow = serializedObject.FindProperty(nameof(throwSafetyOutWindow));
            var pSuppressMetaDataAttributes = serializedObject.FindProperty(nameof(suppressAttributes));
            var pDisableAutoSetup = serializedObject.FindProperty(nameof(disableAutoSetup));

            var fCpp2ilExe = new PropertyField(pCpp2ilExe, "Cpp2IL.exe");
            var fAttemptILToDLL = new PropertyField(pAttemptILToDLL, "Attempt to generate .dll's with IL");
            var fThrowSafetyOutWindow = new PropertyField(pThrowSafetyOutWindow, "Throw safety out the window");
            var fParallel = new PropertyField(pParallel, "Process .dll's simultaneously");
            var fSuppressMetaDataAttributes = new PropertyField(pSuppressMetaDataAttributes, "Suppress Metadata Attributes");
            var fDisableAutoSetup = new PropertyField(pDisableAutoSetup, "Disable Auto-Setup of Import Configuration (Not Recommended)");

            if (pCpp2ilExe.objectReferenceValue == null)
            {
                if (TryFindCPP2ILExecutable(out var executable))
                {
                    pCpp2ilExe.objectReferenceValue = executable;
                    serializedObject.ApplyModifiedProperties();
                }
                else
                {
                    MessageElement.Data = $"***__WARNING__***: Could not find Cpp2IL.exe!";
                    rootVisualElement.Add(MessageElement);
                }
            }


            var callback = new EventCallback<SerializedPropertyChangeEvent>(spce => spce.changedProperty.serializedObject.ApplyModifiedProperties());

            fCpp2ilExe.RegisterValueChangeCallback(new EventCallback<SerializedPropertyChangeEvent>(OnCPP2ILSet));
            fAttemptILToDLL.RegisterValueChangeCallback(callback);
            fThrowSafetyOutWindow.RegisterValueChangeCallback(callback);
            fParallel.RegisterValueChangeCallback(callback);
            fSuppressMetaDataAttributes.RegisterValueChangeCallback(callback);
            fDisableAutoSetup.RegisterValueChangeCallback(callback);

            rootVisualElement.Add(fCpp2ilExe);
            rootVisualElement.Add(fAttemptILToDLL);
            rootVisualElement.Add(fThrowSafetyOutWindow);
            rootVisualElement.Add(fParallel);
            rootVisualElement.Add(fSuppressMetaDataAttributes);
            rootVisualElement.Add(fDisableAutoSetup);

            return rootVisualElement;
        }
    }
}
