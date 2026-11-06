using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

/// Builds the avatar for the DGSNEW website (web/unity/Build/avatar.*).
///
/// Menu:      DGS > Build WebGL for website   (asks for the DGSNEW folder once)
/// Batchmode: Unity -batchmode -quit -projectPath <this project> \
///                  -executeMethod WebGLBuilder.Build -buildPath <DGSNEW>/web/unity
public static class WebGLBuilder
{
    const string PrefKey = "DGS.WebGLBuildPath";

    [MenuItem("DGS/Build WebGL for website")]
    static void BuildFromMenu()
    {
        var path = EditorPrefs.GetString(PrefKey, "");
        if (string.IsNullOrEmpty(path) || !Directory.Exists(Path.GetDirectoryName(path)))
        {
            var root = EditorUtility.OpenFolderPanel("Select the DGSNEW project folder", "", "");
            if (string.IsNullOrEmpty(root)) return;
            path = Path.Combine(root, "web", "unity");
            EditorPrefs.SetString(PrefKey, path);
        }
        BuildTo(path);
    }

    public static void Build()
    {
        var args = Environment.GetCommandLineArgs();
        int i = Array.IndexOf(args, "-buildPath");
        if (i < 0 || i + 1 >= args.Length)
            throw new ArgumentException("missing -buildPath <DGSNEW>/web/unity");
        var report = BuildTo(args[i + 1]);
        if (report.summary.result != UnityEditor.Build.Reporting.BuildResult.Succeeded)
            EditorApplication.Exit(1);
    }

    static UnityEditor.Build.Reporting.BuildReport BuildTo(string path)
    {
        // Gzip + JS fallback: works on any web server, also without Content-Encoding headers.
        PlayerSettings.WebGL.compressionFormat = WebGLCompressionFormat.Gzip;
        PlayerSettings.WebGL.decompressionFallback = true;
        PlayerSettings.WebGL.dataCaching = true;
        PlayerSettings.runInBackground = true;          // keep signing when the tab loses focus

        var scenes = EditorBuildSettings.scenes.Where(s => s.enabled).Select(s => s.path).ToArray();
        if (scenes.Length == 0) scenes = new[] { "Assets/Scenes/SampleScene.unity" };

        var options = new BuildPlayerOptions
        {
            scenes = scenes,
            locationPathName = path,
            target = BuildTarget.WebGL,
            options = BuildOptions.None,
        };
        var report = BuildPipeline.BuildPlayer(options);
        var s = report.summary;
        Debug.Log($"WebGL build {s.result}: {path} ({s.totalSize / (1024 * 1024)} MB, {s.totalTime.TotalSeconds:0}s)");

        // The website expects Build/avatar.* - rename Unity's <folder>.* files.
        var build = Path.Combine(path, "Build");
        if (s.result == UnityEditor.Build.Reporting.BuildResult.Succeeded && Directory.Exists(build))
        {
            var prefix = new DirectoryInfo(path).Name + ".";
            foreach (var f in Directory.GetFiles(build))
            {
                var name = Path.GetFileName(f);
                if (!name.StartsWith(prefix)) continue;
                var target = Path.Combine(build, "avatar." + name.Substring(prefix.Length));
                if (File.Exists(target)) File.Delete(target);
                File.Move(f, target);
            }
        }
        return report;
    }
}
