namespace ReArtWord.Contents.BackgroundService.Contracts.Scripting.Packages;

/// <summary>独立して導入・固定するスクリプト依存関係の配布方式です。</summary>
public enum EScriptPackageKind
{
    /// <summary>C#で参照するNuGetパッケージです。</summary>
    Nuget,

    /// <summary>Pythonの仮想環境へ導入するpipパッケージです。</summary>
    Pip,

    /// <summary>Node.jsから読み込むnpmパッケージです。</summary>
    Npm,
}
