using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;

namespace AutoMobFarm.Ipc;

/// <summary>
/// vnavmesh への窓口。IPC 名と型は vnavmesh のソース（ffxiv_navmesh/vnavmesh/IPCProvider.cs）で確認済み。
///
/// 注意（資料と実装から確かめた事実）:
///  ・経路探索に失敗しても例外は飛ばず、経路が空のまま終わる。「移動失敗」と「移動完了」が IPC 上は区別できない → 距離と制限時間を自分で確かめる。
///  ・Path.Stop は全体に効く（他のプラグインが始めた移動も止まる）。自分が始めた移動のときだけ呼ぶ（<see cref="Farm.OwnPath"/> が終点で見分ける）。
///  ・Nav.PathfindCancelAll の中身はメッシュの再読み込みなので使わない。
///  ・SimpleMove は探索が終わると vnavmesh が自分で歩き出し、止めた後に遅れて歩き出すので使わない（取り消せる探索だけを使う）。
/// </summary>
public sealed class VnavmeshIpc : IpcGate, Farm.INavControl
{
    public override string InternalName => "vnavmesh";

    /// <summary>ナビメッシュが使える状態か（エリア移動直後は読み込み中で false）。</summary>
    public bool IsReady()
        => this.TryInvoke("Nav.IsReady", () => this.Func<bool>("vnavmesh.Nav.IsReady").InvokeFunc(), out var ready) && ready;

    /// <summary>
    /// エリアに入ったとき地図を自動で読み込む設定か（Nav.IsAutoLoad＝Config.AutoLoadNavmesh。IPCProvider.cs 28 行で確認）。読めなければ null。
    /// OFF だと、エリア移動の後は地図が空になり、Nav.Reload を呼んでも何も読み込まない（NavmeshManager.cs の Update・Reload）。
    /// </summary>
    public bool? IsAutoLoad()
        => this.TryInvoke("Nav.IsAutoLoad", () => this.Func<bool>("vnavmesh.Nav.IsAutoLoad").InvokeFunc(), out var v) ? v : null;

    /// <summary>構築の進み具合。0以上1未満＝構築中、負＝構築していない。読めなければ null。</summary>
    public float? BuildProgress()
        => this.TryInvoke("Nav.BuildProgress", () => this.Func<float>("vnavmesh.Nav.BuildProgress").InvokeFunc(), out var p) ? p : null;

    /// <summary>いまのエリアのナビメッシュを読み込ませる（自動読み込みが OFF の環境向け。設定は変えない）。</summary>
    public bool Reload()
        => this.TraceThen("Nav.Reload()") && this.TryInvoke("Nav.Reload", () => this.Func<bool>("vnavmesh.Nav.Reload").InvokeFunc(), out _);

    /// <summary>
    /// 経路だけを求める（取り消せる。Nav.PathfindCancelable、型は Func&lt;Vector3, Vector3, bool, CancellationToken, Task&lt;List&lt;Vector3&gt;&gt;&gt;）。
    /// 求めた経路は、こちらが <see cref="MoveAlong"/> を呼ぶまで使われない。戻り値は必ず Task のまま受け取る。
    /// </summary>
    public bool TryPathfindCancelable(Vector3 from, Vector3 to, bool fly, CancellationToken cancel, out Task<List<Vector3>>? task)
        => this.TryInvoke("Nav.PathfindCancelable",
            () => this.Func<Vector3, Vector3, bool, CancellationToken, Task<List<Vector3>>>("vnavmesh.Nav.PathfindCancelable")
                .InvokeFunc(from, to, fly, cancel), out task);

    /// <summary>求めた経路をたどらせる（Path.MoveTo。他の経路を上書きする）。</summary>
    public bool MoveAlong(List<Vector3> waypoints, bool fly)
        => this.TraceThen($"Path.MoveTo({waypoints.Count} 点, 飛行={fly})") && this.TryAction("Path.MoveTo",
            () => this.Func<List<Vector3>, bool, object>("vnavmesh.Path.MoveTo").InvokeAction(waypoints, fly));

    /// <summary>指定の水平位置の真下で、立てる床の点を返す（Query.Mesh.PointOnFloor）。無ければ null。</summary>
    public Vector3? PointOnFloor(Vector3 p, bool allowUnlandable, float halfExtentXZ)
        => this.TryInvoke("Query.Mesh.PointOnFloor",
            () => this.Func<Vector3, bool, float, Vector3?>("vnavmesh.Query.Mesh.PointOnFloor").InvokeFunc(p, allowUnlandable, halfExtentXZ), out var v)
            ? v
            : null;

    /// <summary>近くのメッシュ上の点のうち、本来の地面とつながっている床の点（Query.Mesh.NearestPointReachable）。無ければ null。</summary>
    public Vector3? NearestPointReachable(Vector3 p, float halfExtentXZ, float halfExtentY)
        => this.TryInvoke("Query.Mesh.NearestPointReachable",
            () => this.Func<Vector3, float, float, Vector3?>("vnavmesh.Query.Mesh.NearestPointReachable").InvokeFunc(p, halfExtentXZ, halfExtentY), out var v)
            ? v
            : null;

    /// <summary>いま移動中か（経路の追従か、経路の計算が進行中）。</summary>
    public bool IsMoving()
    {
        if (this.TryInvoke("Path.IsRunning", () => this.Func<bool>("vnavmesh.Path.IsRunning").InvokeFunc(), out var running) && running)
            return true;
        if (this.TryInvoke("Nav.PathfindInProgress", () => this.Func<bool>("vnavmesh.Nav.PathfindInProgress").InvokeFunc(), out var navBusy) && navBusy)
            return true;
        return false;
    }

    /// <summary>経路をたどって実際に動いている最中か（Path.IsRunning だけ）。読めなければ null（「動いていない」と区別する）。</summary>
    public bool? FollowingPath()
        => this.TryInvoke("Path.IsRunning", () => this.Func<bool>("vnavmesh.Path.IsRunning").InvokeFunc(), out var running) ? running : null;

    /// <summary>いまたどっている経路の残りの点の数（読めなければ null）。</summary>
    public int? NumWaypoints()
        => this.TryInvoke("Path.NumWaypoints", () => this.Func<int>("vnavmesh.Path.NumWaypoints").InvokeFunc(), out var n) ? n : null;

    /// <summary>いまたどっている経路の終点（読めたら true。経路が無ければ end は null）。自分の経路かを確かめるのに使う。</summary>
    public bool TryLastWaypoint(out Vector3? end)
    {
        end = null;
        if (!this.TryInvoke("Path.ListWaypoints", () => this.Func<List<Vector3>>("vnavmesh.Path.ListWaypoints").InvokeFunc(), out var list))
            return false;
        end = list is { Count: > 0 } ? list[^1] : null;
        return true;
    }

    /// <summary>移動を止める。自分が始めた移動のときだけ呼ぶこと（全体に効くため）。</summary>
    public bool Stop()
        => this.TraceThen("Path.Stop()") && this.TryAction("Path.Stop", () => this.Func<object>("vnavmesh.Path.Stop").InvokeAction());
}
