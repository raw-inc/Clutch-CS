namespace ReArtWord.Contents.BackgroundService.Contracts.Management;

/// <summary>保存前の読込みと検証を行うYAMLです。</summary>
/// <param name="Text">元のYAML本文。</param>
public sealed record WorkflowYamlImport(string Text);
