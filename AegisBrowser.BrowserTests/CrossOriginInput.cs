using AegisBrowser;
using CefSharp;
using CefSharp.DevTools.Input;
using CefSharp.DevTools.Page;
using System.Diagnostics;
using System.Text.Json;

namespace AegisBrowser.BrowserTests;

internal static partial class Program
{
    private const string ControlledInputFrame = "aegis-p405-input";

    // A small per-attempt trace. The test-owned fixture supplies all labels;
    // it never records URLs, cookies, arbitrary page content, or credentials.
    private sealed class FrameInputTrace : IDisposable
    {
        private const int Limit = 32;
        private readonly string run = Guid.NewGuid().ToString("N");
        private readonly string scenario;
        private readonly Stopwatch elapsed = Stopwatch.StartNew();
        private int count, dropped;
        internal FrameInputTrace(string scenario) => this.scenario = scenario;
        internal bool Complete => dropped == 0;
        internal void Add(string kind, string status)
        {
            if (count >= Limit) { dropped++; return; }
            Console.WriteLine($"P405_INPUT_EVENT: run={run} scenario={scenario} sequence={++count} elapsed_ms={elapsed.ElapsedMilliseconds} kind={kind} status={status}");
        }
        public void Dispose() => Console.WriteLine($"P405_INPUT_TRACE: run={run} scenario={scenario} recorded={count} dropped={dropped}");
    }

    private static FrameTree? FindControlledFrame(FrameTree tree, string expectedUrl)
    {
        var matches = (tree.ChildFrames ?? []).Where(child =>
            child.Frame.Name == ControlledInputFrame && child.Frame.Url == expectedUrl &&
            child.Frame.ParentId == tree.Frame.Id).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    private static string? ControlledCefFrameId(BrowserView browser, string expectedUrl)
    {
        var instance = browser.GetBrowser();
        if (instance is null || !instance.IsValid) return null;
        if (instance.GetFrameNames().Count(name => name == ControlledInputFrame) != 1) return null;
        using var frame = instance.GetFrameByName(ControlledInputFrame);
        if (frame is null || !frame.IsValid || frame.IsMain || frame.Url != expectedUrl
            || string.IsNullOrEmpty(frame.Identifier)) return null;
        using var parent = frame.Parent;
        return parent is { IsValid: true, IsMain: true } ? frame.Identifier : null;
    }

    // Chromium 154's browser-level Input handler hit-tests across renderer
    // widgets. A zero-delta wheel uses its documented compositor visual-state
    // callback before the mouse events. The ACK is not a click observation.
    private static async Task<bool> DispatchCrossOriginInput(
        BrowserView browser, string expectedUrl, string clickVariable, string scenario)
    {
        using var trace = new FrameInputTrace(scenario);
        if (browser.IsDisposed || !browser.IsBrowserInitialized)
        {
            trace.Add("FRAME_CREATED", "browser_unavailable");
            return false;
        }
        var geometryScript = """
            (()=>{const f=document.getElementById('aegis-p405-input');
              if(!f)return JSON.stringify({exists:false});
              const r=f.getBoundingClientRect(), s=getComputedStyle(f), x=r.left+60, y=r.top+40;
              return JSON.stringify({exists:true,source:f.src,visible:s.display!=='none'&&s.visibility==='visible'&&
                Number(s.opacity)>0&&s.pointerEvents!=='none'&&r.width>60&&r.height>40,
                inViewport:x>=0&&y>=0&&x<innerWidth&&y<innerHeight,
                hit:document.elementFromPoint(x,y)===f,x,y,dpr:devicePixelRatio});})()
            """;
        using var geometry = JsonDocument.Parse(await Js(browser, geometryScript));
        var g = geometry.RootElement;
        if (!g.GetProperty("exists").GetBoolean())
        {
            trace.Add("FRAME_CREATED", "not_observed");
            return false;
        }
        trace.Add("FRAME_CREATED", "dom_element_observed");
        if (g.GetProperty("source").GetString() != expectedUrl || !g.GetProperty("visible").GetBoolean()
            || !g.GetProperty("inViewport").GetBoolean() || !g.GetProperty("hit").GetBoolean())
        {
            trace.Add("INPUT_TARGET_IDENTIFIED", "source_visibility_or_hit_mismatch");
            return false;
        }
        var x = g.GetProperty("x").GetDouble();
        var y = g.GetProperty("y").GetDouble();
        trace.Add("FRAME_READY", "fixture_reported_ready");
        trace.Add("INPUT_TARGET_IDENTIFIED", FormattableString.Invariant($"visible_iframe_at_viewport_css_point css_x={x:F2} css_y={y:F2} dpr={g.GetProperty("dpr").GetDouble():F2}"));
        using var client = browser.GetDevToolsClient();
        try
        {
            var cefFrameId = ControlledCefFrameId(browser, expectedUrl);
            if (cefFrameId is null)
            {
                trace.Add("FRAME_ATTACHED", "cef_named_frame_not_attributed");
                return false;
            }
            trace.Add("FRAME_ATTACHED", "cef_named_child_of_main_frame");
            var tree = await client.Page.GetFrameTreeAsync().WaitAsync(TimeSpan.FromSeconds(8));
            var frame = tree.FrameTree is { } initialTree ? FindControlledFrame(initialTree, expectedUrl) : null;
            var frameId = frame?.Frame.Id;
            var children = tree.FrameTree?.ChildFrames?.ToArray() ?? [];
            var nameMatches = children.Count(child => child.Frame.Name == ControlledInputFrame);
            var urlMatches = children.Count(child => child.Frame.Url == expectedUrl);
            trace.Add("FRAME_TREE", $"{(frameId is null ? "child_not_attributed_in_root_tree" : "unique_named_child")} direct_children={children.Length} name_matches={nameMatches} url_matches={urlMatches}");
            string? targetId = null;
            var rootId = tree.FrameTree?.Frame.Id;
            try
            {
                var targets = await client.Target.GetTargetsAsync().WaitAsync(TimeSpan.FromSeconds(8));
                var matching = targets.TargetInfos.Where(target => target.Type == "iframe" && target.Url == expectedUrl).ToArray();
                var parent = matching.Length == 1 ? matching[0].ParentFrameId : null;
                var parentStatus = string.IsNullOrEmpty(parent) || string.IsNullOrEmpty(rootId) ? "unknown"
                    : parent == rootId ? "matches_root" : "mismatch";
                trace.Add("TARGET_DISCOVERY", $"matching_iframe_targets={matching.Length} parent={parentStatus}");
                if (matching.Length > 1 || parentStatus == "mismatch") return false;
                if (matching.Length == 1) targetId = matching[0].TargetId;
            }
            catch (TimeoutException) { trace.Add("TARGET_DISCOVERY", "bounded_query_timeout"); }

            browser.Focus();
            trace.Add("INPUT_DISPATCHED", "zero_delta_wheel_visual_barrier");
            var wheel = await client.Input.DispatchMouseEventAsync(DispatchMouseEventType.MouseWheel, x, y,
                deltaX: 0, deltaY: 0).WaitAsync(TimeSpan.FromSeconds(8));
            if (!wheel.Success) { trace.Add("INPUT_ACKNOWLEDGED", "visual_barrier_failed"); return false; }
            trace.Add("INPUT_ACKNOWLEDGED", "visual_barrier_acknowledged");

            // Recheck identity and geometry after the asynchronous barrier.
            var current = await client.Page.GetFrameTreeAsync().WaitAsync(TimeSpan.FromSeconds(8));
            var currentFrame = current.FrameTree is { } currentTree ? FindControlledFrame(currentTree, expectedUrl) : null;
            if (targetId is not null)
            {
                var currentTargets = await client.Target.GetTargetsAsync().WaitAsync(TimeSpan.FromSeconds(8));
                if (currentTargets.TargetInfos.Count(target => target.Type == "iframe" && target.Url == expectedUrl
                    && target.TargetId == targetId) != 1)
                {
                    trace.Add("INPUT_TARGET_IDENTIFIED", "devtools_target_changed");
                    return false;
                }
            }
            using var afterBarrier = JsonDocument.Parse(await Js(browser, geometryScript));
            var a = afterBarrier.RootElement;
            if (ControlledCefFrameId(browser, expectedUrl) != cefFrameId
                || (frameId is not null && currentFrame?.Frame.Id != frameId) || !a.GetProperty("exists").GetBoolean()
                || a.GetProperty("source").GetString() != expectedUrl || !a.GetProperty("visible").GetBoolean()
                || !a.GetProperty("inViewport").GetBoolean() || !a.GetProperty("hit").GetBoolean()
                || a.GetProperty("x").GetDouble() != x || a.GetProperty("y").GetDouble() != y)
            {
                trace.Add("INPUT_TARGET_IDENTIFIED", "frame_or_geometry_changed");
                return false;
            }
            trace.Add("INPUT_DISPATCHED", "root_devtools_mouse_sequence");
            var move = await client.Input.DispatchMouseEventAsync(DispatchMouseEventType.MouseMoved, x, y).WaitAsync(TimeSpan.FromSeconds(8));
            var down = await client.Input.DispatchMouseEventAsync(DispatchMouseEventType.MousePressed, x, y,
                button: MouseButton.Left, buttons: 1, clickCount: 1).WaitAsync(TimeSpan.FromSeconds(8));
            var up = await client.Input.DispatchMouseEventAsync(DispatchMouseEventType.MouseReleased, x, y,
                button: MouseButton.Left, buttons: 0, clickCount: 1).WaitAsync(TimeSpan.FromSeconds(8));
            if (!move.Success || !down.Success || !up.Success)
            {
                trace.Add("INPUT_ACKNOWLEDGED", "mouse_command_failed");
                return false;
            }
            trace.Add("INPUT_ACKNOWLEDGED", "commands_accepted_delivery_unproven");
            if (!await ObserveControlledClick(browser, clickVariable))
            {
                trace.Add("CLICK_RECEIVED", "not_observed");
                return false;
            }
            using var click = JsonDocument.Parse(await Js(browser, clickVariable));
            var trusted = click.RootElement.GetProperty("trusted").GetBoolean();
            var active = click.RootElement.GetProperty("active").GetBoolean();
            trace.Add("CLICK_RECEIVED", "fixture_message_from_expected_frame");
            trace.Add("EVENT_TRUSTED", trusted ? "true" : "false");
            trace.Add("USER_ACTIVATION_ACTIVE", active ? "true" : "false");
            Check(trusted && active, "cross-origin controlled input is trusted and transiently activated");
            var finalTree = await client.Page.GetFrameTreeAsync().WaitAsync(TimeSpan.FromSeconds(8));
            var finalFrame = finalTree.FrameTree is { } lastTree ? FindControlledFrame(lastTree, expectedUrl) : null;
            if (ControlledCefFrameId(browser, expectedUrl) != cefFrameId
                || (frameId is not null && finalFrame?.Frame.Id != frameId))
            {
                trace.Add("FRAME_ATTACHED", "frame_changed_after_input");
                return false;
            }
            return trace.Complete;
        }
        catch (TimeoutException)
        {
            trace.Add("INPUT_ACKNOWLEDGED", "bounded_operation_timeout");
            return false;
        }
    }
}
