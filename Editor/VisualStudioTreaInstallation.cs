/*---------------------------------------------------------------------------------------------
 *  Copyright (c) Microsoft Corporation. All rights reserved.
 *  Licensed under the MIT License. See License.txt in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEngine;
using SimpleJSONTrea;
using IOPath = System.IO.Path;

namespace Microsoft.Unity.VisualStudio.Editor
{
	internal class VisualStudioTreaInstallation : VisualStudioInstallation
	{
		private static readonly IGenerator _generator = GeneratorFactory.GetInstance(GeneratorStyle.SDK);
	
		public override bool SupportsAnalyzers
		{
			get
			{
				return true;
			}
		}

		public override Version LatestLanguageVersionSupported
		{
			get
			{
				return new Version(13, 0);
			}
		}

		private string GetExtensionPath()
		{
			var trea = IsPrerelease ? ".trea-insiders" : ".trea";
			var extensionsPath = IOPath.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), trea, "extensions");
			if (!Directory.Exists(extensionsPath))
				return null;

			return Directory
				.EnumerateDirectories(extensionsPath, $"{MicrosoftUnityExtensionId}*") // publisherid.extensionid
				.OrderByDescending(n => n)
				.FirstOrDefault();
		}

		public override string[] GetAnalyzers()
		{
			var vstuPath = GetExtensionPath();
			if (string.IsNullOrEmpty(vstuPath))
				return Array.Empty<string>();

			return GetAnalyzers(vstuPath);
		}

		public override IGenerator ProjectGenerator
		{
			get
			{
				return _generator;
			}
		}

		private static bool IsCandidateForDiscovery(string path)
		{
#if UNITY_EDITOR_OSX
			return Directory.Exists(path) && Regex.IsMatch(path, ".*Trea.*.app$", RegexOptions.IgnoreCase);
#elif UNITY_EDITOR_WIN
			return File.Exists(path) && Regex.IsMatch(path, ".*Trea.*.exe$", RegexOptions.IgnoreCase);
#else
			return File.Exists(path) && path.EndsWith("trea", StringComparison.OrdinalIgnoreCase);
#endif
		}

#if UNITY_EDITOR_OSX
		[System.Runtime.InteropServices.DllImport ("libc")]
		private static extern int readlink(string path, byte[] buffer, int buflen);

		internal static string GetRealPath(string path)
		{
			byte[] buf = new byte[512];
			int ret = readlink(path, buf, buf.Length);
			if (ret == -1) return path;
			char[] cbuf = new char[512];
			int chars = System.Text.Encoding.Default.GetChars(buf, 0, ret, cbuf, 0);
			return new String(cbuf, 0, chars);
		}
#else
		internal static string GetRealPath(string path)
		{
			return path;
		}
#endif

		[Serializable]
		internal class VisualStudioTreaManifest
		{
			public string name;
			public string version;
		}

		public static bool TryDiscoverInstallation(string editorPath, out IVisualStudioInstallation installation)
		{
			installation = null;

			if (string.IsNullOrEmpty(editorPath))
				return false;

			if (!IsCandidateForDiscovery(editorPath))
				return false;

			Version version = null;
			var isPrerelease = false;

			try
			{
				var manifestBase = GetRealPath(editorPath);

#if UNITY_EDITOR_WIN
				// on Windows, editorPath is a file, resources as subdirectory
				manifestBase = IOPath.GetDirectoryName(manifestBase);
#elif UNITY_EDITOR_OSX
				// on Mac, editorPath is a directory
				manifestBase = IOPath.Combine(manifestBase, "Contents");
#else
				// on Linux, editorPath is a file, in a bin sub-directory
				var parent = Directory.GetParent(manifestBase);
				// but we can link to [trea]/trea or [trea]/bin/trea
				manifestBase = parent?.Name == "bin" ? parent.Parent?.FullName : parent?.FullName;
#endif

				if (manifestBase == null)
					return false;

				var manifestFullPath = IOPath.Combine(manifestBase, "resources", "app", "package.json");
				if (File.Exists(manifestFullPath))
				{
					var manifest = JsonUtility.FromJson<VisualStudioTreaManifest>(File.ReadAllText(manifestFullPath));
					Version.TryParse(manifest.version.Split('-').First(), out version);
					isPrerelease = manifest.version.ToLower().Contains("insider");
				}
			}
			catch (Exception)
			{
				// do not fail if we are not able to retrieve the exact version number
			}

			isPrerelease = isPrerelease || editorPath.ToLower().Contains("insider");
			installation = new VisualStudioTreaInstallation()
			{
				IsPrerelease = isPrerelease,
				Name = "Trea AI" + (isPrerelease ? " - Insider" : string.Empty) + (version != null ? $" [{version.ToString(3)}]" : string.Empty),
				Path = editorPath,
				Version = version ?? new Version()
			};

			return true;
		}

		public static IEnumerable<IVisualStudioInstallation> GetVisualStudioInstallations()
		{
			var candidates = new List<string>();

#if UNITY_EDITOR_WIN
			var localAppPath = IOPath.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs");
			var programFiles = IOPath.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));

			foreach (var basePath in new[] {localAppPath, programFiles})
			{
				candidates.Add(IOPath.Combine(basePath, "Trea AI", "Trea.exe"));
				candidates.Add(IOPath.Combine(basePath, "Trea AI Insiders", "Trea - Insiders.exe"));
			}
#elif UNITY_EDITOR_OSX
			var appPath = IOPath.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
			candidates.AddRange(Directory.EnumerateDirectories(appPath, "Trea*.app"));
#elif UNITY_EDITOR_LINUX
			// Well known locations
			candidates.Add("/usr/bin/trea");
			candidates.Add("/bin/trea");
			candidates.Add("/usr/local/bin/trea");
#endif

			foreach (var candidate in candidates.Distinct())
			{
				if (TryDiscoverInstallation(candidate, out var installation))
					yield return installation;
			}
		}

		public override void CreateExtraFiles(string projectDirectory)
		{
			try
			{
				var treaDirectory = IOPath.Combine(projectDirectory.NormalizePathSeparators(), ".trea");
				Directory.CreateDirectory(treaDirectory);

				var enablePatch = !File.Exists(IOPath.Combine(treaDirectory, ".vstupatchdisable"));

				CreateRecommendedExtensionsFile(treaDirectory, enablePatch);
				CreateSettingsFile(treaDirectory, enablePatch);
				CreateLaunchFile(treaDirectory, enablePatch);
			}
			catch (IOException)
			{
			}			
		}

		private const string DefaultLaunchFileContent = @"{
    ""version"": ""0.2.0"",
    ""configurations"": [
        {
            ""name"": ""Attach to Unity"",
            ""type"": ""vstuc"",
            ""request"": ""attach""
        }
     ]
}";

		private static void CreateLaunchFile(string treaDirectory, bool enablePatch)
		{
			var launchFile = IOPath.Combine(treaDirectory, "launch.json");
			if (File.Exists(launchFile))
			{
				if (enablePatch)
					PatchLaunchFile(launchFile);

				return;
			}

			File.WriteAllText(launchFile, DefaultLaunchFileContent);
		}

		private static void PatchLaunchFile(string launchFile)
		{
			try
			{
				const string configurationsKey = "configurations";
				const string typeKey = "type";

				var content = File.ReadAllText(launchFile);
				var launch = JSONNode.Parse(content);

				var configurations = launch[configurationsKey] as JSONArray;
				if (configurations == null)
				{
					configurations = new JSONArray();
					launch.Add(configurationsKey, configurations);
				}

				if (configurations.Linq.Any(entry => entry.Value[typeKey] == "vstuc"))
					return;

				var configuration = new JSONObject();
				configuration.Add("name", "Attach to Unity");
				configuration.Add("type", "vstuc");
				configuration.Add("request", "attach");

				configurations.Add(configuration);
				WriteAllTextFromJObject(launchFile, launch);
			}
			catch (Exception)
			{
				// do not fail if we cannot patch the launch.json file
			}
		}

		private const string DefaultSettingsFileContent = @"{
    ""files.exclude"":
    {
        ""**/.git"":true,
        ""**/.DS_Store"":true,
        ""**/*.meta"":true,
        ""**/*.*.meta"":true,
        ""**/*.unity"":true,
        ""**/*.unityproj"":true,
        ""**/*.mat"":true,
        ""**/*.fbx"":true,
        ""**/*.FBX"":true,
        ""**/*.tga"":true,
        ""**/*.cubemap"":true,
        ""**/*.prefab"":true,
        ""**/Library"":true,
        ""**/ProjectSettings"":true,
        ""**/Temp"":true
    }
}";

		private static void CreateSettingsFile(string treaDirectory, bool enablePatch)
		{
			var settingsFile = IOPath.Combine(treaDirectory, "settings.json");
			if (File.Exists(settingsFile))
			{
				if (enablePatch)
					PatchSettingsFile(settingsFile);

				return;
			}

			File.WriteAllText(settingsFile, DefaultSettingsFileContent);
		}

		private static void PatchSettingsFile(string settingsFile)
		{
			try
			{
				const string filesExcludeKey = "files.exclude";

				var content = File.ReadAllText(settingsFile);
				var settings = JSONNode.Parse(content);

				var filesExclude = settings[filesExcludeKey] as JSONObject;
				if (filesExclude == null)
				{
					filesExclude = new JSONObject();
					settings.Add(filesExcludeKey, filesExclude);
				}

				var defaultSettings = JSONNode.Parse(DefaultSettingsFileContent);
				var defaultFilesExclude = defaultSettings[filesExcludeKey] as JSONObject;

				if (defaultFilesExclude == null)
					return;

				foreach (var entry in defaultFilesExclude)
				{
					if (filesExclude[entry.Key] == null)
						filesExclude.Add(entry.Key, entry.Value);
				}

				WriteAllTextFromJObject(settingsFile, settings);
			}
			catch (Exception)
			{
				// do not fail if we cannot patch the settings.json file
			}
		}

		private const string MicrosoftUnityExtensionId = "visualstudiotoolsforunity.vstuc";

		private const string DefaultRecommendedExtensionsContent = @"{
    ""recommendations"": [
      """+ MicrosoftUnityExtensionId + @"""
    ]
}
";

		private static void CreateRecommendedExtensionsFile(string treaDirectory, bool enablePatch)
		{
			// see https://tattoocoder.com/recommending-vscode-extensions-within-your-open-source-projects/
			var extensionFile = IOPath.Combine(treaDirectory, "extensions.json");
			if (File.Exists(extensionFile))
			{
				if (enablePatch)
					PatchRecommendedExtensionsFile(extensionFile);

				return;
			}

			File.WriteAllText(extensionFile, DefaultRecommendedExtensionsContent);
		}

		private static void PatchRecommendedExtensionsFile(string extensionFile)
		{
			try
			{
				const string recommendationsKey = "recommendations";

				var content = File.ReadAllText(extensionFile);
				var extensions = JSONNode.Parse(content);

				var recommendations = extensions[recommendationsKey] as JSONArray;
				if (recommendations == null)
				{
					recommendations = new JSONArray();
					extensions.Add(recommendationsKey, recommendations);
				}

				if (recommendations.Linq.Any(entry => entry.Value.Value == MicrosoftUnityExtensionId))
					return;

				recommendations.Add(MicrosoftUnityExtensionId);
				WriteAllTextFromJObject(extensionFile, extensions);
			}
			catch (Exception)
			{
				// do not fail if we cannot patch the extensions.json file
			}
		}

		private static void WriteAllTextFromJObject(string file, JSONNode node)
		{
			using (var fs = File.Open(file, FileMode.Create))
			using (var sw = new StreamWriter(fs))
			{
				// Keep formatting/indent in sync with default contents
				sw.Write(node.ToString(aIndent: 4));
			}
		}

		public override bool Open(string path, int line, int column, string solution)
		{
			// var application = Path;

			// line = Math.Max(1, line);
			// column = Math.Max(0, column);

			// var directory = IOPath.GetDirectoryName(solution);
			// var workspace = TryFindWorkspace(directory);

			// var target = workspace ?? directory;

			// ProcessRunner.Start(string.IsNullOrEmpty(path)
			// 	? ProcessStartInfoFor(application, $"\"{ target}\"")
			// 	: ProcessStartInfoFor(application, $"\"{ target}\" -g \"{path}\":{line}:{column}"));

			// return true;
				line = Math.Max(1, line);
			column = Math.Max(0, column);

			var directory = IOPath.GetDirectoryName(solution);
			var application = Path;

			var existingProcess = FindRunningCursorWithSolution(directory);
			if (existingProcess != null) {
				try {
					var args = string.IsNullOrEmpty(path) ? 
						$"--reuse-window \"{directory}\"" : 
						$"--reuse-window -g \"{path}\":{line}:{column}";
					
					ProcessRunner.Start(ProcessStartInfoFor(application, args));
					return true;
				}
				catch (Exception ex) {
					UnityEngine.Debug.LogError($"[Cursor] Error using existing instance: {ex}");
				}
			}

			var newArgs = string.IsNullOrEmpty(path) ?
				$"--new-window \"{directory}\"" :
				$"--new-window \"{directory}\" -g \"{path}\":{line}:{column}";
			
			ProcessRunner.Start(ProcessStartInfoFor(application, newArgs));
			return true;
			
		}

		private static string TryFindWorkspace(string directory)
		{
			var files = Directory.GetFiles(directory, "*.code-workspace", SearchOption.TopDirectoryOnly);
			if (files.Length == 0 || files.Length > 1)
				return null;

			return files[0];
		}

		private static ProcessStartInfo ProcessStartInfoFor(string application, string arguments)
		{
#if UNITY_EDITOR_OSX
			// wrap with built-in OSX open feature
			arguments = $"-n \"{application}\" --args {arguments}";
			application = "open";
			return ProcessRunner.ProcessStartInfoFor(application, arguments, redirect:false, shell: true);
#else
			return ProcessRunner.ProcessStartInfoFor(application, arguments, redirect: false);
#endif
		}

		public static void Initialize()
		{
		}

		private Process FindRunningCursorWithSolution(string solutionPath) {
			var normalizedTargetPath = solutionPath.Replace('\\', '/').TrimEnd('/').ToLowerInvariant();
			
#if UNITY_EDITOR_WIN
			// Keep as is for Windows platform since path already includes drive letter
#else
			// Ensure path starts with / for macOS and Linux platforms
			if (!normalizedTargetPath.StartsWith("/")) {
				normalizedTargetPath = "/" + normalizedTargetPath;
			}
#endif
			
			var processes = new List<Process>();
			
			// Get process name list based on different operating systems
#if UNITY_EDITOR_OSX
			processes.AddRange(Process.GetProcessesByName("Trae"));
			processes.AddRange(Process.GetProcessesByName("Trae Helper"));
#elif UNITY_EDITOR_LINUX
			processes.AddRange(Process.GetProcessesByName("Trae"));
			processes.AddRange(Process.GetProcessesByName("Trae"));
#else
			processes.AddRange(Process.GetProcessesByName("trae"));
#endif
			
			foreach (var process in processes) {
				try {
					var workspaces = ProcessRunner.GetProcessWorkspaces(process);
					if (workspaces != null && workspaces.Length > 0) {
						foreach (var workspace in workspaces) {
							var normalizedWorkspaceDir = workspace.Replace('\\', '/').TrimEnd('/').ToLowerInvariant();
							
#if UNITY_EDITOR_WIN
							// Keep as is for Windows platform
#else
							// Ensure path starts with / for macOS and Linux platforms
							if (!normalizedWorkspaceDir.StartsWith("/")) {
								normalizedWorkspaceDir = "/" + normalizedWorkspaceDir;
							}
#endif

							if (string.Equals(normalizedWorkspaceDir, normalizedTargetPath, StringComparison.OrdinalIgnoreCase) ||
								normalizedTargetPath.StartsWith(normalizedWorkspaceDir + "/", StringComparison.OrdinalIgnoreCase) ||
								normalizedWorkspaceDir.StartsWith(normalizedTargetPath + "/", StringComparison.OrdinalIgnoreCase))
							{
								return process;
							}
						}
					}
				}
				catch (Exception ex) {
					UnityEngine.Debug.LogError($"[Cursor] Error checking process: {ex}");
					continue;
				}
			}
			return null;
		}

	}
}