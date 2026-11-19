namespace Concierge.Shared.Tools;

public sealed record ConciergeCodeSetup(string Id, string Title, string Summary, IReadOnlyList<string> Extensions);

public static class ConciergeCodeSetups
{
    public static readonly IReadOnlyList<ConciergeCodeSetup> All =
    [
        new("c", "C", "C editing and code navigation.",
            ["llvm-vs-code-extensions.vscode-clangd"]),
        new("cpp", "C++", "C++ editing and code navigation.",
            ["llvm-vs-code-extensions.vscode-clangd"]),
        new("rust", "Rust", "Rust analysis, completion, and navigation.",
            ["rust-lang.rust-analyzer"]),
        new("swift", "Swift", "Swift editing with SourceKit-LSP.",
            ["swiftlang.swift-vscode"]),
        new("go", "Go", "Go editing, tests, and debugging.",
            ["golang.Go"]),
        new("csharp", "C#", "C# editing, debugging, and project formatting.",
            ["muhammad-sammy.csharp", "EditorConfig.EditorConfig"]),
        new("java", "Java", "Java editing, refactoring, and project support.",
            ["redhat.java"]),
        new("kotlin", "Kotlin", "Kotlin completion, linting, and debugging.",
            ["fwcd.kotlin"]),
        new("arkts", "ArkTS", "ArkTS and ETS language support.",
            ["arkts-dev.arkts-syntax-highlighter"]),
        new("python", "Python", "Python editing, debugging, and linting.",
            ["ms-python.python", "ms-python.debugpy", "charliermarsh.ruff"]),
    ];

    public static ConciergeCodeSetup Find(string id)
        => All.FirstOrDefault(setup => string.Equals(setup.Id, id, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException("Unknown Concierge Code setup.", nameof(id));
}
