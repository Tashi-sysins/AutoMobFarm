namespace AutoMobFarm.Ipc;

/// <summary>
/// RotationSolverReborn（RSR）への窓口：命令を送ることと、内部の値を読むことだけ（判断は Farm\RsrController）。
///
/// 【名前の前置詞】IPC の前置詞は "RotationSolverReborn"。プラグインの InternalName は "RotationSolver"。
/// 【型】導入版 7.5.6.19 の DLL を MetadataLoadContext で読んで確認した：
///   void ChangeOperatingMode(StateCommandType : byte)   Off=0, Auto=1, TargetOnly=2, Manual=3, AutoDuty=4, Henched=5, PvP=6
///   void OtherCommand(OtherCommandType : byte, string)  Settings=0（"AoEType Off" / "TargetFreely false" など）
///   bool AutorotationActive()
/// byte で渡すと Dalamud が型を合わせて渡す（実機の戦闘で使っている呼び方）。実際の反映はこの窓口では保証しない：
/// RsrController が内部の値（RsrStateReader）を読み直して確かめる。
/// 【Henched は無攻撃ではない】Henched はハードターゲットを殴る（RSR の RSCommands_StateSpecialCommand.cs）。攻撃を止めるのはモード Off。
/// </summary>
public sealed class RotationSolverIpc : IpcGate, Farm.IRotationControl
{
    public override string InternalName => "RotationSolver";

    public override string DisplayName => "RotationSolverReborn";

    private const string Prefix = "RotationSolverReborn.";

    public bool DisableTargetFreelyOverride()
        => this.TraceThen("DisableTargetFreelyOverride()") && this.TryAction("DisableTargetFreelyOverride",
            () => this.Func<object>(Prefix + "DisableTargetFreelyOverride").InvokeAction());

    public bool SendMode(byte mode)
        => this.TraceThen($"ChangeOperatingMode({Farm.RsrController.ModeName(mode)})") && this.TryAction("ChangeOperatingMode",
            () => this.Func<byte, object>(Prefix + "ChangeOperatingMode").InvokeAction(mode));

    public bool SendAoe(byte value)
        => this.TraceThen($"OtherCommand(Settings, AoEType {Farm.RsrController.AoeName(value)})") && this.TryAction("OtherCommand",
            () => this.Func<byte, string, object>(Prefix + "OtherCommand").InvokeAction(0, $"AoEType {Farm.RsrController.AoeName(value)}"));

    public bool SendBool(string name, bool value)
        => this.TraceThen($"OtherCommand(Settings, {name} {(value ? "true" : "false")})") && this.TryAction("OtherCommand",
            () => this.Func<byte, string, object>(Prefix + "OtherCommand").InvokeAction(0, $"{name} {(value ? "true" : "false")}"));

    public byte? ReadMode() => RsrStateReader.ReadMode();

    public byte? ReadAoe() => RsrStateReader.ReadAoeType();

    public bool? ReadBool(string name) => RsrStateReader.ReadBool(name);

    public bool? ReadTargetFreelyOverride() => RsrStateReader.ReadTargetFreelyOverride();

    public string? LastReadError => RsrStateReader.LastError;

    /// <summary>自動ローテーションが動いているか（画面用）。読めなければ null。</summary>
    public bool? IsActive()
        => this.TryInvoke("AutorotationActive", () => this.Func<bool>(Prefix + "AutorotationActive").InvokeFunc(), out var v) ? v : null;
}
