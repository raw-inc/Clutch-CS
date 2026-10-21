namespace ReArtWord.Contents.BackgroundService.Runtime.Nodes;

/// <summary>外部への通信・書込み・状態変更を伴わず、実行時の入力や時刻・乱数から値を返します。</summary>
/// <remarks>値選択を通した要求時評価を許可する契約です。設定時の静的評価はIValueNodeだけが提供します。</remarks>
public interface IRuntimeValueNode : IActionNode;
