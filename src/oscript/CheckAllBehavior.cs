/*----------------------------------------------------------
This Source Code Form is subject to the terms of the
Mozilla Public License, v.2.0. If a copy of the MPL
was not distributed with this file, You can obtain one
at http://mozilla.org/MPL/2.0/.
----------------------------------------------------------*/

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using OneScript.Compilation.Binding;
using OneScript.Contexts;
using OneScript.DependencyInjection;
using OneScript.Language;
using OneScript.Language.SyntaxAnalysis;
using ScriptEngine;
using ScriptEngine.Compiler;
using ScriptEngine.HostedScript;
using ScriptEngine.Machine.Contexts;

namespace oscript
{
    /// <summary>
    /// Проверка синтаксиса нескольких файлов за один запуск: движок инициализируется один раз.
    /// В отличие от -check, ошибки модуля собираются все, а неизвестные символы
    /// (глобальный контекст 1С, общие модули) не останавливают проверку и выводятся отдельной строкой.
    /// Коды возврата: 0 - ошибок нет, 1 - есть ошибки, 2 - хотя бы один файл не удалось проверить.
    /// </summary>
    internal class CheckAllBehavior : AppBehavior
    {
        private const string SymbolNotFoundId = nameof(LocalizedErrors.SymbolNotFound);
        private const int UnknownSymbolsShown = 10;

        private readonly IReadOnlyList<string> _paths;

        public CheckAllBehavior(IReadOnlyList<string> paths)
        {
            _paths = paths;
        }

        public override int Execute()
        {
            var files = ExpandPaths(_paths);
            if (files.Count == 0)
            {
                Output.WriteLine("No files to check.");
                return 2;
            }

            var builder = ConsoleHostBuilder.Create(files[0]);
            var hostedScript = ConsoleHostBuilder.Build(builder);
            hostedScript.Initialize();
            hostedScript.SetGlobalEnvironment(new DoNothingHost(), hostedScript.Loader.FromFile(files[0]));

            int withErrors = 0, notChecked = 0;
            foreach (var file in files)
            {
                var result = CheckFile(hostedScript, file);
                foreach (var line in result.Lines)
                    Output.WriteLine(line);

                if (result.Failed)
                    notChecked++;
                else if (result.HasErrors)
                    withErrors++;
            }

            Output.WriteLine($"Files: {files.Count}, with errors: {withErrors}, not checked: {notChecked}");
            return notChecked > 0 ? 2 : withErrors > 0 ? 1 : 0;
        }

        private static FileResult CheckFile(HostedScriptEngine hostedScript, string file)
        {
            var result = new FileResult();
            var errors = new ListErrorSink();
            try
            {
                using var scope = hostedScript.Services.CreateScope();
                var compiler = CreateCompiler(hostedScript, scope, errors);
                var source = hostedScript.Loader.FromFile(file);
                compiler.Compile(source, hostedScript.Engine.NewProcess());
            }
            catch (ScriptException e)
            {
                // лексер и препроцессор прерывают разбор исключением: это тоже ошибка модуля
                errors.AddError(new CodeError
                {
                    ErrorId = "Fatal",
                    Description = e.ErrorDescription,
                    Position = e.GetPosition()
                });
            }
            catch (BindingException e)
            {
                // повторное объявление символа: привязка сообщает о нём исключением, без позиции
                errors.AddError(new CodeError
                {
                    ErrorId = "Binding",
                    Description = e.Message,
                    Position = new ErrorPositionInfo { LineNumber = 0, ColumnNumber = 0 }
                });
            }
            catch (Exception e)
            {
                // после синтаксических ошибок генератор кода может упасть на неполном дереве:
                // собранные ошибки разбора при этом действительны
                if (!errors.Errors.Any(err => err.ErrorId != SymbolNotFoundId))
                {
                    result.Failed = true;
                    result.Lines.Add($"{file}: not checked: {e.GetType().Name}: {e.Message}");
                    return result;
                }
            }

            var all = errors.Errors.ToList();
            var unknown = all.Where(e => e.ErrorId == SymbolNotFoundId).ToList();
            var real = all.Where(e => e.ErrorId != SymbolNotFoundId)
                .GroupBy(e => (e.Position?.LineNumber, e.Position?.ColumnNumber, e.Description))
                .Select(g => g.First())
                .ToList();

            foreach (var error in real)
            {
                var line = error.Position?.LineNumber ?? 0;
                var column = error.Position?.ColumnNumber ?? 0;
                result.Lines.Add($"{file}: Ошибка в строке: {line},{column} / {error.Description}");
            }
            result.HasErrors = real.Count > 0;

            if (!result.HasErrors)
                result.Lines.Add($"{file}: No errors.");

            if (unknown.Count > 0)
            {
                var names = unknown.Select(e => e.Description).Distinct().ToList();
                var shown = string.Join("; ", names.Take(UnknownSymbolsShown));
                var more = names.Count > UnknownSymbolsShown ? $"; ... +{names.Count - UnknownSymbolsShown}" : "";
                result.Lines.Add($"{file}: unknown symbols: {unknown.Count} ({shown}{more})");
            }

            return result;
        }

        private static CompilerFrontend CreateCompiler(HostedScriptEngine hostedScript, IServiceContainer scope, IErrorSink errors)
        {
            // Символы и определения препроцессора - как у штатного компилятора -check,
            // обработчик ошибок - собственный, чтобы не прерываться на первой ошибке.
            var standard = hostedScript.GetCompilerService();
            var compiler = new CompilerFrontend(
                scope.Resolve<PreprocessorHandlers>(),
                errors,
                scope,
                scope.Resolve<IDependencyResolver>(),
                scope.Resolve<PredefinedInterfaceResolver>(),
                scope.Resolve<OneScriptCoreOptions>());

            compiler.SharedSymbols = standard.SharedSymbols;
            compiler.FillSymbols(typeof(UserScriptContextInstance));
            foreach (var definition in standard.PreprocessorDefinitions)
                compiler.PreprocessorDefinitions.Add(definition);

            return compiler;
        }

        private static List<string> ExpandPaths(IEnumerable<string> paths)
        {
            var files = new List<string>();
            foreach (var path in paths)
            {
                if (Directory.Exists(path))
                {
                    files.AddRange(Directory.EnumerateFiles(path, "*.*", SearchOption.AllDirectories)
                        .Where(f => f.EndsWith(".bsl", StringComparison.OrdinalIgnoreCase)
                                    || f.EndsWith(".os", StringComparison.OrdinalIgnoreCase))
                        .OrderBy(f => f, StringComparer.Ordinal));
                }
                else
                {
                    files.Add(path);
                }
            }

            return files;
        }

        public static AppBehavior Create(CmdLineHelper helper)
        {
            var paths = new List<string>();
            string arg;
            while ((arg = helper.Next()) != null)
                paths.Add(arg);

            return paths.Count == 0 ? null : new CheckAllBehavior(paths);
        }

        private class FileResult
        {
            public List<string> Lines { get; } = new List<string>();
            public bool HasErrors { get; set; }
            public bool Failed { get; set; }
        }
    }
}
