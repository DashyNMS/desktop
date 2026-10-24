using System.Net.Http;
using System.Text.Json;
using DesktopNMS.Core.SignIn;

namespace DesktopNMS.Services;

/// <summary>Drive the shared token flow without acting on a page overtaken by navigation.</summary>
public sealed class WebSignInSession(WebTokenSignIn flow, Func<Uri> location,
    Func<string, Task<string?>> script, Action<Uri> navigate, Action<WebSignInResult> finish,
    TimeSpan? settleTime = null) : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _closed = new();
    private int _generation;
    private int _handled = -1;
    private bool _awaitingPost;
    private bool _postSent;

    public void NavigationStarted() => _generation++;

    public void RequestStarted(Uri uri, HttpMethod method)
    {
        if (method == HttpMethod.Post && flow.IsOnServer(uri)
            && uri.AbsolutePath.TrimEnd('/') == flow.TokensPage.AbsolutePath.TrimEnd('/')) _postSent = true;
    }

    public async Task PageLoadedAsync()
    {
        var generation = _generation;
        try
        {
            await _gate.WaitAsync(_closed.Token);
            try
            {
                if (generation != _generation || generation == _handled || _closed.IsCancellationRequested) return;
                _handled = generation;
                var page = location();
                if (!flow.IsOnServer(page)) return;
                var probe = await script(Guard(WebTokenSignIn.ProbeScript));
                if (generation != _generation || _closed.IsCancellationRequested) return;
                if (_awaitingPost && !_postSent) flow.SubmitNotSent();
                _awaitingPost = false;
                var step = flow.Next(page, probe);
                switch (step.Action)
                {
                    case WebSignInAction.GoToTokensPage:
                        navigate(flow.TokensPage);
                        break;
                    case WebSignInAction.Submit:
                        await Task.Delay(settleTime ?? TimeSpan.FromSeconds(1), _closed.Token);
                        if (generation != _generation) { flow.SubmitSkipped(); return; }
                        _postSent = false;
                        _awaitingPost = true;
                        var sent = await script(Guard(flow.SubmitScript));
                        if (generation == _generation && WebTokenSignIn.Unwrap(sent) != "submitted")
                        {
                            flow.SubmitNotSent();
                            _awaitingPost = false;
                        }
                        break;
                    case WebSignInAction.Done:
                        finish(new(WebSignInOutcome.Token, step.Token));
                        break;
                    case WebSignInAction.NotAllowed:
                        finish(new(WebSignInOutcome.NotAllowed));
                        break;
                    case WebSignInAction.Failed:
                        finish(new(WebSignInOutcome.Failed));
                        break;
                }
            }
            finally { _gate.Release(); }
        }
        catch (OperationCanceledException) when (_closed.IsCancellationRequested) { }
        catch (Exception) { if (!_closed.IsCancellationRequested) finish(new(WebSignInOutcome.Failed)); }
    }

    // Check the document's actual origin too: it may change while native script execution is queued.
    private string Guard(string expression) => "(function(){if(location.origin!=="
        + JsonSerializer.Serialize(flow.TokensPage.GetLeftPart(UriPartial.Authority))
        + ")return null;return " + expression + ";})()";

    public void Dispose() => _closed.Cancel();
}
