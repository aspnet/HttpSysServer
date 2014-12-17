using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using Microsoft.AspNet.Razor;
using Microsoft.AspNet.Razor.Generator;
using Microsoft.Framework.CodeGeneration.Templating;
using Microsoft.Framework.CodeGeneration.Templating.Compilation;
using Microsoft.Framework.DependencyInjection;
using Microsoft.Framework.Runtime;

namespace RazorCodeGeneration
{
    public class Program
    {
        private const int NumArgs = 1;
        private ICompilationService _compilationService;

        public Program(IServiceProvider sp)
        {
            var appEnv = sp.GetService<IApplicationEnvironment>();
            var loadContextAccessor = sp.GetService<IAssemblyLoadContextAccessor>();
            var libMan = sp.GetService<ILibraryManager>();
            _compilationService = new RoslynCompilationService(appEnv, loadContextAccessor, libMan);
        }

        public void Main(string[] args)
        {
            if (args.Length != NumArgs)
            {
                throw new ArgumentException(string.Format("Requires {0} argument (Library Name), {1} given", NumArgs, args.Length));
            }

            var dir = args[0];
            var csTemplates = GetCsTemplates(dir);

            var fileCount = 0;
            foreach (var fileName in csTemplates)
            {
                Console.WriteLine("  Generating code file for template {0}...", Path.GetFileName(fileName));
                GenerateCodeFile(fileName);
                Console.WriteLine("      Done!");
                fileCount++;
            }

            Console.WriteLine();
            Console.WriteLine("{0} files successfully generated.", fileCount);
            Console.WriteLine();
        }

        private IEnumerable<string> GetCsTemplates(string path)
        {
            if (!Directory.Exists(path))
            {
                throw new ArgumentException("path");
            }

            return Directory.EnumerateFiles(path, "*.cshtml");
        }

        private void GenerateCodeFile(string cstemplatePath)
        {
            var basePath = Path.GetDirectoryName(cstemplatePath);
            var fileName = Path.GetFileName(cstemplatePath);
            var fileNameNoExtension = Path.GetFileNameWithoutExtension(fileName);

            string templateSource;
            using (var fileStream = File.OpenText(cstemplatePath))
            {
                templateSource = GenerateTemplateCode(basePath, fileNameNoExtension, fileName, fileStream);
            }

            var templateResult = _compilationService.Compile(templateSource);
            if (templateResult.Messages.Any())
            {
                throw new Exception(string.Join("\n", templateResult.Messages));
            }

            var compiledObject = (RazorTemplateBase) Activator.CreateInstance(templateResult.CompiledType);
            var source = compiledObject.ExecuteTemplate().Result;

            File.WriteAllText(Path.Combine(basePath, string.Format("{0}.Generated.cs", fileNameNoExtension)), source);
        }

        private string GenerateTemplateCode(string basePath, string className, string fileName, StreamReader fileStream)
        {
            var codeLang = new CSharpRazorCodeLanguage();
            var host = new RazorEngineHost(codeLang)
            {
                DefaultBaseClass = typeof(RazorTemplateBase).FullName,

                GeneratedClassContext = new GeneratedClassContext(
                    executeMethodName: "ExecuteAsync",
                    writeMethodName: "Write",
                    writeLiteralMethodName: "WriteLiteral",
                    writeToMethodName: "WriteTo",
                    writeLiteralToMethodName: "WriteLiteralTo",
                    templateTypeName: "",
                    generatedTagHelperContext: new GeneratedTagHelperContext())
            };

            var engine = new RazorTemplateEngine(host);

            var code = engine.GenerateCode(
                input: fileStream,
                className: className,
                rootNamespace: "RazorCodeGeneration.Template",
                sourceFileName: fileName);

            return code.GeneratedCode;
        }
    }
}
