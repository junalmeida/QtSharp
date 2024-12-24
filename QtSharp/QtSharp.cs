using System.Reflection;
using System.Text;
using CppSharp;
using CppSharp.AST;
using CppSharp.AST.Extensions;
using CppSharp.Generators;
using CppSharp.Passes;
using CppSharp.Utils;

namespace QtSharp
{
    public class QtSharp : ILibrary
    {
        public QtSharp(QtInfo qtInfo, bool debug)
        {
            this.qtInfo = qtInfo;
            this.debug = debug;
        }

        public ICollection<KeyValuePair<string, string>> GetVerifiedWrappedModules()
        {
            for (int i = this.wrappedModules.Count - 1; i >= 0; i--)
            {
                var wrappedModule = this.wrappedModules[i];
                if (!File.Exists(wrappedModule.Key) || !File.Exists(wrappedModule.Value))
                {
                    this.wrappedModules.RemoveAt(i);
                }
            }
            return this.wrappedModules;
        }

        public void Preprocess(Driver driver, ASTContext lib)
        {
            foreach (var unit in lib.TranslationUnits.Where(u => u.IsValid))
            {
                IgnorePrivateDeclarations(unit);
            }

            lib.FindFunction("QtSharedPointer::weakPointerFromVariant_internal").First().ExplicitlyIgnore();
            lib.FindFunction("QtSharedPointer::sharedPointerFromVariant_internal").First().ExplicitlyIgnore();

            // QString is type-mapped to string so we only need two methods for the conversion
            var qString = lib.FindCompleteClass("QString");
            qString.HasNonTrivialCopyConstructor = false;
            foreach (var @class in qString.Declarations)
            {
                @class.ExplicitlyIgnore();
            }
            foreach (var method in qString.Methods.Where(m => m.OriginalName != "utf16" && m.OriginalName != "fromUtf16"))
            {
                method.ExplicitlyIgnore();
            }

            // HACK: work around https://github.com/mono/CppSharp/issues/594
            lib.FindCompleteClass("QGraphicsItem").FindEnum("Extension").Access = AccessSpecifier.Public;
            lib.FindCompleteClass("QAbstractSlider").FindEnum("SliderChange").Access = AccessSpecifier.Public;
            lib.FindCompleteClass("QAbstractItemView").FindEnum("CursorAction").Access = AccessSpecifier.Public;
            lib.FindCompleteClass("QAbstractItemView").FindEnum("State").Access = AccessSpecifier.Public;
            lib.FindCompleteClass("QAbstractItemView").FindEnum("DropIndicatorPosition").Access = AccessSpecifier.Public;
            var classesWithTypeEnums = new[]
                                       {
                                           "QGraphicsEllipseItem", "QGraphicsItemGroup", "QGraphicsLineItem",
                                           "QGraphicsPathItem", "QGraphicsPixmapItem", "QGraphicsPolygonItem",
                                           "QGraphicsProxyWidget", "QGraphicsRectItem", "QGraphicsSimpleTextItem",
                                           "QGraphicsTextItem", "QGraphicsWidget", "QGraphicsSvgItem"
                                       };
            foreach (var enumeration in from @class in classesWithTypeEnums
                                        from @enum in lib.FindCompleteClass(@class).Enums
                                        where string.IsNullOrEmpty(@enum.Name)
                                        select @enum)
            {
                enumeration.Name = "TypeEnum";
            }
        }

        public void Postprocess(Driver driver, ASTContext ctx)
        {
            new ClearCommentsPass().VisitASTContext(driver.Context.ASTContext);
            var modules = this.qtInfo.LibFiles.Select(GetModuleNameFromLibFile);
            var s = System.Diagnostics.Stopwatch.StartNew();
            new GetCommentsFromQtDocsPass(this.qtInfo.Docs, modules).VisitASTContext(driver.Context.ASTContext);
            Console.WriteLine("Documentation done in: {0}", s.Elapsed);

            var qChar = ctx.FindCompleteClass("QChar");
            var op = qChar.FindOperator(CXXOperatorKind.ExplicitConversion)
                .FirstOrDefault(o => o.Parameters[0].Type.IsPrimitiveType(PrimitiveType.Char));
            if (op != null)
                op.ExplicitlyIgnore();
            op = qChar.FindOperator(CXXOperatorKind.Conversion)
                .FirstOrDefault(o => o.Parameters[0].Type.IsPrimitiveType(PrimitiveType.Int));
            if (op != null)
                op.ExplicitlyIgnore();
            // QString is type-mapped to string so we only need two methods for the conversion
            // go through the methods a second time to ignore free operators moved to the class
            var qString = ctx.FindCompleteClass("QString");
            foreach (var method in qString.Methods.Where(
                m => !m.Ignore && m.OriginalName != "utf16" && m.OriginalName != "fromUtf16"))
            {
                method.ExplicitlyIgnore();
            }

            foreach (var module in driver.Options.Modules)
            {
                var prefix = Platform.IsWindows ? string.Empty : "lib";
                var extension = Platform.IsWindows ? ".dll" : Platform.IsMacOS ? ".dylib" : ".so";
                var inlinesLibraryFile = $"{prefix}{module.SymbolsLibraryName}{extension}";
                var inlinesLibraryPath = Path.Combine(driver.Options.OutputDir, Platform.IsWindows ? "release" : string.Empty, inlinesLibraryFile);
                this.wrappedModules.Add(new KeyValuePair<string, string>(module.LibraryName + ".dll", inlinesLibraryPath));
            }
        }

        public void Setup(Driver driver)
        {
            driver.Options.Verbose = debug;
            driver.Options.GenerateDebugOutput = debug;
            driver.ParserOptions.Verbose = debug;

            driver.ParserOptions.MicrosoftMode = false;
            driver.ParserOptions.NoBuiltinIncludes = true;
            driver.ParserOptions.TargetTriple = this.qtInfo.Target;
            driver.ParserOptions.UnityBuild = true;
            driver.ParserOptions.SkipPrivateDeclarations = false;
            driver.ParserOptions.LanguageVersion = CppSharp.Parser.LanguageVersion.CPP17;

            driver.Options.GeneratorKind = GeneratorKind.CSharp;
            driver.Options.CheckSymbols = true;
            driver.Options.CompileCode = true;
            driver.Options.GenerateDefaultValuesForArguments = true;
            driver.Options.GenerateClassTemplates = true;
            driver.Options.MarshalCharAsManagedChar = true;
            driver.Options.CommentKind = CommentKind.BCPLSlash;

            string dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            const string qt = "Qt";
            if (this.qtInfo.LibFiles.Count == 0)
                throw new Exception("No Qt libraries found");
            foreach (var libFile in this.qtInfo.LibFiles)
            {
                string qtModule = GetModuleNameFromLibFile(libFile);
                var module = driver.Options.AddModule($"QtSharp.{qtModule}");
                module.Headers.Add(qtModule);

                var moduleName = qtModule.Substring(qt.Length);

                // some Qt modules have their own name-spaces
                if (moduleName == "Charts" || moduleName == "DataVisualization" ||
                    moduleName.StartsWith("3D", StringComparison.Ordinal))
                {
                    module.OutputNamespace = string.Empty;
                    module.SymbolsLibraryName = $"{qtModule}-symbols";
                }
                else
                {
                    module.OutputNamespace = qtModule;
                }
                if (Platform.IsMacOS)
                {
                    var framework = $"{qtModule}.framework";
                    module.IncludeDirs.Add(Path.Combine(this.qtInfo.Libs, framework));
                    module.IncludeDirs.Add(Path.Combine(this.qtInfo.Libs, framework, "Headers"));
                    if (moduleName == "UiPlugin")
                    {
                        var qtUiPlugin = $"Qt{moduleName}.framework";
                        module.IncludeDirs.Add(Path.Combine(this.qtInfo.Libs, qtUiPlugin));
                        module.IncludeDirs.Add(Path.Combine(this.qtInfo.Libs, qtUiPlugin, "Headers"));
                    }
                }
                else
                {
                    var moduleInclude = Path.Combine(qtInfo.Headers, qtModule);
                    if (Directory.Exists(moduleInclude))
                        module.IncludeDirs.Add(moduleInclude);
                    if (moduleName == "Designer")
                    {
                        module.IncludeDirs.Add(Path.Combine(qtInfo.Headers, "QtUiPlugin"));
                    }

                }
                if (moduleName == "Designer" && Directory.Exists(module.IncludeDirs.Last()))
                {
                    foreach (var header in Directory.EnumerateFiles(module.IncludeDirs.Last(), "*.h"))
                    {
                        module.Headers.Add(Path.GetFileName(header));
                    }
                }
                module.Libraries.Add(libFile);
                if (moduleName == "Core")
                {
                    if (Platform.IsWindows)
                        module.Headers.Insert(0, "guiddef.h");
                    module.CodeFiles.Add(Path.Combine(dir, "QObject.cs"));
                    module.CodeFiles.Add(Path.Combine(dir, "QEvent.cs"));
                }

                module.LibraryDirs.Add(Platform.IsWindows ? qtInfo.Bins : qtInfo.Libs);

                if (module.IncludeDirs.Count == 0)
                {
                    driver.Options.Modules.Remove(module);
                }
            }

            foreach (var systemIncludeDir in this.qtInfo.SystemIncludeDirs)
                driver.ParserOptions.AddSystemIncludeDirs(systemIncludeDir);

            if (Platform.IsMacOS)
            {
                foreach (var frameworkDir in this.qtInfo.FrameworkDirs)
                    driver.ParserOptions.AddArguments($"-F{frameworkDir}");
                driver.ParserOptions.AddArguments($"-F{qtInfo.Libs}");
            }
            driver.ParserOptions.AddIncludeDirs(this.qtInfo.Headers);
        }

        public static string GetModuleNameFromLibFile(string libFile)
        {
            var qtModule = Path.GetFileNameWithoutExtension(libFile);
            if (qtModule.StartsWith("lib"))
                qtModule = qtModule.Substring("lib".Length);
            while (qtModule.Contains("."))
                qtModule = Path.GetFileNameWithoutExtension(qtModule);

            if (int.TryParse(qtModule[2].ToString(), out var _))
                return "Qt" + qtModule.Substring("Qt".Length + 1);
            else
                return qtModule;
        }

        public void SetupPasses(Driver driver)
        {
            driver.Context.TranslationUnitPasses.AddPass(new GenerateSignalEventsPass(driver.Generator));
            driver.Context.TranslationUnitPasses.AddPass(new GenerateEventEventsPass(driver.Generator));
            driver.Context.TranslationUnitPasses.AddPass(new RemoveQObjectMembersPass());

            var generateSymbolsPass = driver.Context.TranslationUnitPasses.FindPass<GenerateSymbolsPass>();
            generateSymbolsPass.SymbolsCodeGenerated += (sender, e) =>
                                                        {
                                                            e.OutputDir = driver.Context.Options.OutputDir;
                                                            this.CompileMakefile(e);
                                                        };
        }

        private static void IgnorePrivateDeclarations(DeclarationContext unit)
        {
            foreach (var declaration in unit.Declarations)
            {
                IgnorePrivateDeclaration(declaration);
            }
        }

        private static void IgnorePrivateDeclaration(Declaration declaration)
        {
            if (declaration.Name != null &&
                (declaration.Name.StartsWith("Private", StringComparison.Ordinal) ||
                 declaration.Name.EndsWith("Private", StringComparison.Ordinal)))
            {
                declaration.ExplicitlyIgnore();
            }
            else
            {
                var declarationContext = declaration as DeclarationContext;
                if (declarationContext != null)
                {
                    IgnorePrivateDeclarations(declarationContext);
                }
            }
        }

        private void CompileMakefile(GenerateSymbolsPass.SymbolsCodeEventArgs e)
        {
            var pro = $"{e.Module.SymbolsLibraryName}.pro";
            var path = Path.Combine(e.OutputDir, pro);
            var proBuilder = new StringBuilder();
            var qtModules = string.Join(" ", from header in e.Module.Headers
                                             where !header.EndsWith(".h", StringComparison.Ordinal)
                                             select header.Substring("Qt".Length).ToLowerInvariant());
            // QtTest is only library which has a "lib" suffix to its module alias for qmake
            if (qtModules == "test")
            {
                qtModules += "lib";
            }

            proBuilder.Append("QT += ").Append(qtModules).Append("\n");
            proBuilder.Append("QMAKE_CXXFLAGS += -Wa,-mbig-obj\n");
            proBuilder.Append("CONFIG += c++11\n");
            proBuilder.Append("TARGET = ").Append(e.Module.SymbolsLibraryName).Append("\n");
            proBuilder.Append("TEMPLATE = lib\n");
            proBuilder.Append("SOURCES += ").Append(Path.ChangeExtension(pro, "cpp")).Append("\n");
            if (Environment.OSVersion.Platform == PlatformID.Win32NT)
            {
                proBuilder.Append("LIBS += -loleaut32 -lole32");
            }
            File.WriteAllText(path, proBuilder.ToString());

            int error;
            string errorMessage;
            ProcessHelper.Run(this.qtInfo.QMake, $"\"{path}\"", out error, out errorMessage);
            if (!string.IsNullOrEmpty(errorMessage))
            {
                Console.WriteLine(errorMessage);
                return;
            }
            var makefile = File.Exists(Path.Combine(e.OutputDir, "Makefile.Release")) ? "Makefile.Release" : "Makefile";
            e.CustomCompiler = this.qtInfo.Make;
            e.CompilerArguments = $"-f {makefile}";
            e.OutputDir = Platform.IsMacOS ? e.OutputDir : Path.Combine(e.OutputDir, "release");
        }

        private readonly QtInfo qtInfo;
        private readonly bool debug;
        private List<KeyValuePair<string, string>> wrappedModules = new List<KeyValuePair<string, string>>();
    }
}
