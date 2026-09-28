<h1 align="center">OneScript — preprocessor fix for skipped #If branches</h1>

<h4 align="center">A fork of <a href="https://github.com/EvilBeaver/OneScript">OneScript</a> 2.2.0 that stops taking a <code>#</code> inside strings and comments of an inactive <code>#Если</code> branch for a preprocessor directive</h4>

<div align="center">
  <a href="https://github.com/EvilBeaver/OneScript"><img src="https://img.shields.io/badge/upstream-EvilBeaver%2FOneScript-blue" alt="Upstream" /></a>
  <a href="LICENSE"><img src="https://img.shields.io/badge/license-MPL--2.0-green" alt="License: MPL-2.0" /></a>
  <a href="https://github.com/EvilBeaver/OneScript/releases/tag/v2.2.0"><img src="https://img.shields.io/badge/based%20on-v2.2.0-lightgrey" alt="Based on v2.2.0" /></a>
</div>
<br/>

OneScript is an independent cross-platform virtual machine that runs scripts written in the 1C:Enterprise language, without the 1C:Enterprise platform. Its `oscript -check` is also a handy syntax check for 1C modules. This fork keeps everything OneScript 2.2.0 does and fixes one preprocessor bug that shows up exactly there: code in an inactive `#Если` branch that contains a `#` in a string or a comment.

Maintained by **Ivan Bokhan**, on top of OneScript 2.2.0 by Andrei Ovsiankin (EvilBeaver) and the OneScript contributors — see [Credits](#credits).

Upstream documentation: [README-EN.md](README-EN.md) (English), [README-RU.md](README-RU.md) (Russian), [oscript.io](https://oscript.io).

## Changes from upstream

One method changes: `FindHashSign` in `src/OneScript.Language/SyntaxAnalysis/ConditionalDirectiveHandler.cs`. Regression tests are added to `src/Tests/OneScript.Language.Tests/PreprocessorTests.cs`. Nothing else in the engine is touched.

### The bug

```bsl
#Если Клиент Тогда
А = "цвет #000000";
#КонецЕсли
```

| Engine | `oscript -check` |
|---|---|
| OneScript 1.9.4 | `No errors.` |
| OneScript 2.2.0 | `Ошибка в строке: 2,12 / Ожидается директива препроцессора` |
| this fork | `No errors.` |

`Клиент` is not defined in OneScript, so the branch is inactive and its text should not be parsed at all. When an `#Если` condition is false, `ConditionalDirectiveHandler` skips the branch without lexing it and looks for the next `#`. `FindHashSign` checked `SourceCodeIterator.OnNewLine`, but that flag is updated only when a lexeme is read. After `ReadToLineEnd` it stayed `true` for the whole skipped branch, so any `#` — in a string, in a comment, after code — was taken for a directive.

The same root cause produced other symptoms:

| Text in an inactive branch | OneScript 2.2.0 | 1.9.4 and this fork |
|---|---|---|
| `А = "#КонецЕсли";` | false error: the block is closed early, the rest of the line is parsed as code | no errors |
| `\|#КонецЕсли` in a multi-line string | false error `Неизвестный символ \|` | no errors |
| `// #КонецЕсли` and no real `#КонецЕсли` | **false success** | `Ожидается директива препроцессора #КонецЕсли` |
| `А = 1; #КонецЕсли` and no real `#КонецЕсли` | **false success** | `Ожидается директива препроцессора #КонецЕсли` |
| LF file: a `#КонецОбласти` line right before `#КонецЕсли` | false error `Недопустимое начало директивы препроцессора` | no errors |

The last case is a second defect in the same function. `FindHashSign` stepped over the newline under the iterator with `MoveNext`, so `SkipSpaces` never saw it and the private `_onNewLine` flag stayed `false`; the directive lexer then rejected the real `#КонецЕсли`. With CRLF the iterator stands on `\r` at that point, which is why Windows files were not affected.

### The fix

A `#` is a directive only when it is the first non-whitespace character of a line — the same rule the lexer applies to active code. The start of a line is detected by a change of `CurrentLine`, and every whitespace character, including the one under the iterator on entry, goes through `SkipSpaces`, so the directive lexer sees the new line. The contract of `SourceCodeIterator` is unchanged, the text of an inactive branch is still not parsed, and errors in active code stay errors.

## Verification

* **Tests first.** 28 new tests in `PreprocessorTests` (14 scenarios, CRLF and LF): strings, doubled quotes, multi-line strings, comments, indentation, nested blocks, `#ИначеЕсли`/`#Иначе`, a missing `#КонецЕсли`, and active code. On the upstream code 23 of them fail. With the fix all 198 tests of `OneScript.Language.Tests` pass.
* **Control build.** The same commit without the fix was built with the same command, to separate the fix from the build environment. It behaves exactly like the official 2.2.0 in every check below.
* **Engine regression.** The other unit test projects pass. `tests/testrunner.os -runall` (1,092 tests) gives the same result for every test as the control build.
* **Real code.** 11,275 modules of a 1C:Enterprise 8.3 configuration and its archived builds were checked with `oscript -check` by both engines. The official 2.2.0 reported a false preprocessor error in 90 of them (30 distinct modules): colours like `#000000`, code templates like `"&&$##"`, query text placeholders like `#Поле`. With the fix these errors are gone, and on each of the 90 modules the result and the error line match OneScript 1.9.4. No module got worse. Check time per file did not change.

`oscript -check` stops at the first error, and on 1C modules that is usually `Неизвестный символ` — the 1C global context does not exist in OneScript. A false preprocessor error is visible only when it comes first, so 90 is a lower bound.

## Building

Requires the .NET 8 SDK or newer.

```shell
git clone https://github.com/ivanbokhan84/OneScript.git
cd OneScript
dotnet test src/Tests/OneScript.Language.Tests/OneScript.Language.Tests.csproj -c Release -p:Platform=AnyCPU
dotnet publish src/oscript/oscript.csproj -r win-x64 --self-contained -c Release -p:VersionPrefix=2.2.0 -p:VersionSuffix=fork.1 -o dist/bin
```

This builds `oscript` only — enough for `oscript -check`. For a full distribution, including the C++ Native API component and the standard library packages, use the upstream `Build.csproj` targets.

## Known differences

* A `#` that starts a line in an inactive branch is still a directive: `#000000` alone on a line is an error, as in 2.2.0. OneScript 1.9.4 skips such a line. The line-start rule is kept on purpose.
* The fix has not been submitted upstream yet.

## License

[Mozilla Public License 2.0](LICENSE), the same as upstream. Modified files keep their license headers. The engine change is a single commit on top of `v2.2.0`; the other commits in this branch touch documentation only.

## Credits

* [OneScript](https://github.com/EvilBeaver/OneScript) by Andrei Ovsiankin (EvilBeaver) and the OneScript contributors.
* The bug was found while building [Vanteam BSL Check](https://github.com/ivanbokhan84/vanteam-bsl-check), which uses `oscript -check` as the quick level of its checks.
