using System.Net;
using System.Reflection;
using System.Text.Json;
using KanbanFlow.Components.Pages;
using KanbanFlow.Components.Shared;
using KanbanFlow.Models;
using KanbanFlow.Services;
using Microsoft.AspNetCore.Components;

namespace KanbanFlow.Tests;

// Exercise the real component handlers with deferred dependencies, following the
// TaskDescriptionRenderingTests convention. These are not browser/rendering tests.
public sealed class ComponentLoadFailureTests
{
    [Fact]
    public async Task IdeaLoadFailure_IsRetainedInTheActiveDialog()
    {
        var ideas = new DeferredIdeas();
        await using var modal = new IdeaModal();
        Inject(modal, "IdeaSvc", ideas);
        Parameters(modal, (nameof(IdeaModal.IsOpen), true), (nameof(IdeaModal.EditIdeaId), 1));
        var load = InvokeAsync(modal, "OnParametersSetAsync");
        ideas.Reads[1].SetException(new InvalidOperationException("database unavailable"));
        await load;
        Assert.Equal("Load failed: database unavailable", Field(modal, "errorMessage"));
        Assert.Equal(false, Field(modal, "isLoading"));
        Assert.Null(Field(modal, "expectedWorkflowVersion"));
    }

    [Fact]
    public async Task SupersededIdeaFailure_DoesNotEscapeOrReplaceTheNewDialogState()
    {
        var ideas = new DeferredIdeas();
        await using var modal = new IdeaModal();
        Inject(modal, "IdeaSvc", ideas);
        Parameters(modal, (nameof(IdeaModal.IsOpen), true), (nameof(IdeaModal.EditIdeaId), 1));
        var oldLoad = InvokeAsync(modal, "OnParametersSetAsync");
        Parameters(modal, (nameof(IdeaModal.EditIdeaId), 2));
        var currentLoad = InvokeAsync(modal, "OnParametersSetAsync");
        Assert.True(ideas.Tokens[1].IsCancellationRequested);
        ideas.Reads[1].SetException(new InvalidOperationException("old failure"));
        await oldLoad;
        Assert.Null(Field(modal, "errorMessage"));
        Assert.Equal(true, Field(modal, "isLoading"));
        ideas.Reads[2].SetException(new InvalidOperationException("current failure"));
        await currentLoad;
        Assert.Equal("Load failed: current failure", Field(modal, "errorMessage"));
        Assert.Equal(false, Field(modal, "isLoading"));
    }

    [Fact]
    public async Task ReplacedAiRequest_CannotClearLoadingOrPublishAnOldError()
    {
        using var http = new DeferredHttp();
        await using var view = CreateView(http);
        var oldLoad = InvokeAsync(view, "ShowProjectContextAsync");
        var currentLoad = InvokeAsync(view, "ShowProjectContextAsync");
        Assert.True(http.Requests[0].Token.IsCancellationRequested);
        Assert.Equal("/api/projects/1/context", http.Requests[1].Path);
        http.Requests[0].Response.SetException(new HttpRequestException("old HTTP failure"));
        await oldLoad;
        Assert.Null(Field(view, "aiPanelError"));
        Assert.Equal(true, Field(view, "aiLoading"));
        http.Requests[1].Response.SetResult(JsonResponse("current project"));
        await currentLoad;
        Assert.Equal(false, Field(view, "aiLoading"));
        var context = JsonSerializer.SerializeToElement(Field(view, "aiContext"));
        Assert.Equal("current project", context.GetProperty("Project").GetProperty("Name").GetString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ClosingOrDisposingAiPanel_CancelsAndDiscardsLateResponses(bool dispose)
    {
        using var http = new DeferredHttp();
        var view = CreateView(http);
        try
        {
            var load = InvokeAsync(view, "ShowProjectContextAsync");
            if (dispose) await view.DisposeAsync();
            else Invoke(view, "CloseAiPanel");
            Assert.True(http.Requests[0].Token.IsCancellationRequested);
            http.Requests[0].Response.SetResult(JsonResponse("obsolete project"));
            await load;
            Assert.Null(Field(view, "aiContext"));
            Assert.Null(Field(view, "aiPanelError"));
            Assert.Equal(false, Field(view, "aiLoading"));
        }
        finally { if (!dispose) await view.DisposeAsync(); }
    }

    private static ProjectView CreateView(DeferredHttp http)
    {
        var view = new ProjectView();
        Parameters(view, (nameof(ProjectView.Id), 1));
        Inject(view, "Nav", new TestNavigation());
        Inject(view, "HttpClientFactory", http);
        return view;
    }

    private static HttpResponseMessage JsonResponse(string name) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(new { project = new { name } }), System.Text.Encoding.UTF8, "application/json")
    };

    private static void Parameters(object component, params (string Name, object? Value)[] values) =>
        ParameterView.FromDictionary(values.ToDictionary(v => v.Name, v => v.Value)).SetParameterProperties(component);

    private const BindingFlags Members = BindingFlags.Instance | BindingFlags.NonPublic;
    private static object? Invoke(object component, string name) => component.GetType().GetMethod(name, Members)!.Invoke(component, null);
    private static Task InvokeAsync(object component, string name) => Assert.IsAssignableFrom<Task>(Invoke(component, name));
    private static object? Field(object component, string name) => component.GetType().GetField(name, Members)!.GetValue(component);
    private static void Inject(object component, string name, object value) => component.GetType().GetProperty(name, Members)!.SetValue(component, value);

    private sealed class TestNavigation : NavigationManager
    {
        public TestNavigation() => Initialize("http://localhost/", "http://localhost/project/1");
        protected override void NavigateToCore(string uri, bool forceLoad) => throw new NotSupportedException();
    }

    private sealed class DeferredHttp : HttpMessageHandler, IHttpClientFactory
    {
        public List<(string Path, CancellationToken Token, TaskCompletionSource<HttpResponseMessage> Response)> Requests { get; } = [];
        public HttpClient CreateClient(string name) => new(this, disposeHandler: false);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
            Requests.Add((request.RequestUri!.AbsolutePath, cancellationToken, response));
            // Intentionally ignore cancellation to reproduce a dependency completing late.
            return response.Task;
        }
    }

    private sealed class DeferredIdeas : IIdeaService
    {
        public Dictionary<int, TaskCompletionSource<Idea?>> Reads { get; } = [];
        public Dictionary<int, CancellationToken> Tokens { get; } = [];
        public Task<Idea?> GetByIdAsync(int ideaId, CancellationToken ct = default)
        {
            Tokens[ideaId] = ct;
            Reads[ideaId] = new(TaskCreationOptions.RunContinuationsAsynchronously);
            return Reads[ideaId].Task;
        }
        public Task<Idea> CreateAsync(IdeaCreate dto, string? actor, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<Idea>> ListAsync(IdeaStatus? status = null, string? tag = null, string? search = null, int limit = 100, int offset = 0, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Idea> UpdateAsync(int ideaId, IdeaUpdate dto, string? actor, CancellationToken ct = default) => throw new NotSupportedException();
        public Task DeleteAsync(int ideaId, string? actor, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IdeaPromoteResult> PromoteAsync(int ideaId, string? actor, int? existingProjectId = null, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
