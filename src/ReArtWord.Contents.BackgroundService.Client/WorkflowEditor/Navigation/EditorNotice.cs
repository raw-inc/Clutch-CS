namespace ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Navigation;

/// <summary>表示の寿命と用途、現在言語で組み立てる文です。</summary>
internal sealed record EditorNotice(Guid Id, string Channel, Func<string> Text, bool Persistent);
