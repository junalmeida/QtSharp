using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.RegularExpressions;
using CppSharp;
using CppSharp.Utils;

namespace QtSharp.CLI
{
    public class Program
    {
        static int ParseArgs(string[] args, out string qmake, out string make, out bool debug)
        {
            // TODO: Replace with a proper command line parser.
            qmake = null;
            make = null;
            debug = false;

            qmake = "qmake";
            if (!File.Exists(qmake))
            {
                Console.WriteLine("The specified qmake does not exist.");
                return 1;
            }

            make = "make";
            if (!File.Exists(make))
            {
                Console.WriteLine("The specified make does not exist.");
                return 1;
            }

            debug = true;

            return 0;
        }

        static List<QtInfo> FindQt()
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.Personal);
            var qts = new List<QtInfo>();

            var qtPaths = new[] { Path.Combine(home, "Qt"), "/usr/include/x86_64-linux-gnu/qt6" };

            if (Platform.IsLinux) // support Qt6 on Linux
                return new[] {
                    new QtInfo {
                        IsSystemPackage = true,
                        QMake = "/usr/bin/qmake6",
                        Make = "/usr/bin/make"
                    }
                }.ToList();

            foreach (var qtPath in qtPaths)
            {
                if (!Directory.Exists(qtPath))
                    continue;

                foreach (var path in Directory.EnumerateDirectories(qtPath))
                {
                    var dir = Path.GetFileName(path);
                    bool isNumber = dir.All(c => char.IsDigit(c) || c == '.');
                    if (!isNumber)
                        continue;
                    var qt = new QtInfo { Path = path };
                    var match = Regex.Match(dir, @"([0-9]+)\.([0-9]+)");
                    if (!match.Success)
                        continue;
                    qt.MajorVersion = int.Parse(match.Groups[1].Value);
                    qt.MinorVersion = int.Parse(match.Groups[2].Value);
                    qts.Add(qt);
                }
            }

            return qts;
        }

        static bool QueryQt(QtInfo qt, bool debug)
        {
            // check for OS X
            if (!qt.IsSystemPackage)
            {
                if (string.IsNullOrWhiteSpace(qt.QMake))
                {
                    var osx = Path.Combine(qt.Path, "clang_64/bin/qmake");
                    if (File.Exists(osx))
                    {
                        qt.QMake = osx;
                    }
                }
                if (string.IsNullOrWhiteSpace(qt.Make))
                {
                    qt.Make = "/usr/bin/make";
                }

                string path = Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.Machine);
                path = Path.GetDirectoryName(qt.Make) + Path.PathSeparator + path;
                Environment.SetEnvironmentVariable("Path", path, EnvironmentVariableTarget.Process);
            }

            int error;
            string errorMessage;


            System.Version.TryParse(ProcessHelper.Run(qt.QMake, "-query QT_VERSION", out error, out errorMessage), out var version);
            if (!string.IsNullOrEmpty(errorMessage))
            {
                Console.WriteLine(errorMessage);
                return false;
            }
            qt.MajorVersion = version.Major;
            qt.MinorVersion = version.Minor;

            qt.Bins = ProcessHelper.Run(qt.QMake, "-query QT_INSTALL_BINS", out error, out errorMessage);
            if (!string.IsNullOrEmpty(errorMessage))
            {
                Console.WriteLine(errorMessage);
                return false;
            }

            qt.Libs = ProcessHelper.Run(qt.QMake, "-query QT_INSTALL_LIBS", out error, out errorMessage);
            if (!string.IsNullOrEmpty(errorMessage))
            {
                Console.WriteLine(errorMessage);
                return false;
            }

            DirectoryInfo libsInfo = new DirectoryInfo(Platform.IsWindows ? qt.Bins : qt.Libs);
            if (!libsInfo.Exists)
            {
                Console.WriteLine(
                    "The directory \"{0}\" that qmake returned as the lib directory of the Qt installation, does not exist.",
                    libsInfo.Name);
                return false;
            }
            qt.LibFiles = GetLibFiles(libsInfo, qt.MajorVersion, debug);

            qt.Headers = ProcessHelper.Run(qt.QMake, "-query QT_INSTALL_HEADERS", out error, out errorMessage);
            if (!string.IsNullOrEmpty(errorMessage))
            {
                Console.WriteLine(errorMessage);
                return false;
            }
            DirectoryInfo headersInfo = new DirectoryInfo(qt.Headers);
            if (!headersInfo.Exists)
            {
                Console.WriteLine(
                    "The directory \"{0}\" that qmake returned as the header direcory of the Qt installation, does not exist.",
                    headersInfo.Name);
                return false;
            }

            qt.Docs = ProcessHelper.Run(qt.QMake, "-query QT_INSTALL_DOCS", out error, out errorMessage);

            string emptyFile = Platform.IsWindows ? "NUL" : "/dev/null";
            string output;
            ProcessHelper.Run("gcc", $"-v -E -x c++ {emptyFile}", out error, out output);
            qt.Target = Regex.Match(output, @"Target:\s*(?<target>[^\r\n]+)").Groups["target"].Value;

            const string includeDirsRegex = @"#include <\.\.\.> search starts here:(?<includes>.+)End of search list";
            string allIncludes = Regex.Match(output, includeDirsRegex, RegexOptions.Singleline).Groups["includes"].Value;
            var includeDirs = allIncludes.Split(new[] { Environment.NewLine }, StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim()).ToList();

            const string frameworkDirectory = "(framework directory)";
            qt.SystemIncludeDirs = includeDirs.Where(s => !s.Contains(frameworkDirectory))
                .Select(Path.GetFullPath);

            if (Platform.IsMacOS)
                qt.FrameworkDirs = includeDirs.Where(s => s.Contains(frameworkDirectory))
                    .Select(s => s.Replace(frameworkDirectory, string.Empty).Trim()).Select(Path.GetFullPath);

            return true;
        }

        public static int Main(string[] args)
        {
            Stopwatch s = Stopwatch.StartNew();
            var qts = FindQt();
            bool found = qts.Count != 0;
            bool debug = true;
            QtInfo qt = null;

            if (!found)
            {
                qt = new QtInfo();

                var result = ParseArgs(args, out qt.QMake, out qt.Make, out debug);
                if (result != 0)
                    return result;
            }
            else
            {
                // TODO: Only for OSX for now, generalize for all platforms.
                foreach (var currentQt in qts)
                {
                    if (QueryQt(currentQt, debug))
                    {
                        qt = currentQt;
                        break;
                    }
                }
            }

            if (qt == null)
            {
                Console.Error.WriteLine("Qt installation not found.");
                return 1;
            }

            bool log = false;
            ConsoleLogger logredirect = log ? new ConsoleLogger() : null;
            if (logredirect != null)
                logredirect.CreateLogDirectory();

            for (int i = qt.LibFiles.Count - 1; i >= 0; i--)
            {
                var libFile = qt.LibFiles[i];
                var libFileName = Path.GetFileNameWithoutExtension(libFile);
                if (Path.GetExtension(libFile) == ".exe" ||
                    // QtQuickTest is a QML module but has 3 main C++ functions and is not auto-ignored
                    libFileName == "QtQuickTest" || libFileName == "Qt5QuickTest" || libFileName == "Qt6QuickTest")
                {
                    qt.LibFiles.RemoveAt(i);
                }
            }
            var qtSharp = new QtSharp(qt, debug);
            ConsoleDriver.Run(qtSharp);
            var wrappedModules = qtSharp.GetVerifiedWrappedModules();

            if (wrappedModules.Count == 0)
            {
                Console.WriteLine("Generation failed.");
                return 1;
            }

            const string qtSharpZip = "QtSharp.zip";
            if (File.Exists(qtSharpZip))
            {
                File.Delete(qtSharpZip);
            }
            using (var zip = File.Create(qtSharpZip))
            {
                using (var zipArchive = new ZipArchive(zip, ZipArchiveMode.Create))
                {
                    foreach (var wrappedModule in wrappedModules)
                    {
                        zipArchive.CreateEntryFromFile(wrappedModule.Key, wrappedModule.Key);
                        var documentation = Path.ChangeExtension(wrappedModule.Key, "xml");
                        zipArchive.CreateEntryFromFile(documentation, documentation);
                        zipArchive.CreateEntryFromFile(wrappedModule.Value, Path.GetFileName(wrappedModule.Value));
                    }
                    zipArchive.CreateEntryFromFile("CppSharp.Runtime.dll", "CppSharp.Runtime.dll");
                }
            }
            Console.WriteLine("Done in: " + s.Elapsed);
            return 0;
        }

        private static IList<string> GetLibFiles(DirectoryInfo libsInfo, int major, bool debug)
        {
            List<string> modules;

            if (Platform.IsMacOS)
            {
                modules = libsInfo.EnumerateDirectories("*.framework").Select(dir => Path.GetFileNameWithoutExtension(dir.Name)).ToList();
            }
            else if (Platform.IsLinux)
            {
                modules = (from file in libsInfo.EnumerateFiles()
                           where Regex.IsMatch(file.Name, $@"^libQt\d?\w+\.so\.{major}.*$") && file.LinkTarget == null
                           select file.Name).ToList();
                //TODO: Remove
                modules = modules.Where(x => x.Contains($"Qt{major}Core")).ToList();
            }
            else
            {
                modules = (from file in libsInfo.EnumerateFiles()
                           where Regex.IsMatch(file.Name, @"^Qt\d?\w+\.\w+$")
                           select file.Name).ToList();
            }

            // for (var i = modules.Count - 1; i >= 0; i--)
            // {
            //     var module = Path.GetFileNameWithoutExtension(modules[i]);
            //     if (debug && module != null && !module.EndsWith("d", StringComparison.Ordinal))
            //     {
            //         modules.Remove($"{module}{Path.GetExtension(modules[i])}");
            //     }
            //     else
            //     {
            //         modules.Remove($"{module}d{Path.GetExtension(modules[i])}");
            //     }
            // }
            return modules;
        }
    }
}
