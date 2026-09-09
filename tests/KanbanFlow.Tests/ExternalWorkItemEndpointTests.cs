using KanbanFlow.Models;
using KanbanFlow.Services;
using Microsoft.AspNetCore.Http;
using System.Text.Json;

namespace KanbanFlow.Tests;

public sealed class ExternalWorkItemEndpointTests
{
    [Fact]
    public void StructuredProjection_PreservesStateAndUsesDeterministicOrder()
    {
        var quarantined = Link(id: 8, taskId: 2, state: ExternalLinkState.Quarantined);
        quarantined.QuarantineReason = "Title-only candidate";
        quarantined.ExternalNodeId = null;
        var active = Link(id: 3, taskId: 1, state: ExternalLinkState.Active);

        var result = ApiEndpoints.MapExternalWorkItemLinks([quarantined, active]);

        Assert.Collection(
            result,
            first =>
            {
                Assert.Equal(3, first.Id);
                Assert.Equal("GitHub", first.Provider);
                Assert.Equal("Active", first.State);
                Assert.Equal(336, first.ExternalNumber);
            },
            second =>
            {
                Assert.Equal(8, second.Id);
                Assert.Equal("Quarantined", second.State);
                Assert.Equal("Title-only candidate", second.QuarantineReason);
            });
    }

    [Fact]
    public void GitHubUrlValidation_RejectsHostOrIdentityMismatch()
    {
        Assert.Throws<ValidationException>(() => ApiEndpoints.ValidateGitHubIssueUrl(
            "https://example.com/joelfuller2016/KanbanFlow-v2/issues/336", 336));

        Assert.Throws<ValidationException>(() => ApiEndpoints.ValidateGitHubIssueUrl(
            "https://github.com/joelfuller2016/KanbanFlow-v2/issues/999",
            336,
            "joelfuller2016",
            "KanbanFlow-v2"));

        Assert.Throws<ValidationException>(() => ApiEndpoints.ValidateGitHubIssueUrl(
            "https://attacker@github.com/joelfuller2016/KanbanFlow-v2/issues/336",
            336,
            "joelfuller2016",
            "KanbanFlow-v2"));

        ApiEndpoints.ValidateGitHubIssueUrl(
            "https://github.com/joelfuller2016/KanbanFlow-v2/issues/336",
            336,
            "joelfuller2016",
            "KanbanFlow-v2");
    }

    [Fact]
    public void ExternalWriteProjection_StampsSqliteUnspecifiedInstantsAsUtc()
    {
        var storedUtcTicks = DateTime.SpecifyKind(
            new DateTime(2026, 8, 30, 21, 45, 0),
            DateTimeKind.Unspecified);
        var operation = new ExternalWriteOperation
        {
            OperationId = Guid.Parse("11111111-1111-4111-8111-111111111111"),
            TaskId = 7,
            ProjectId = 3,
            Provider = ExternalProvider.GitHub,
            RepositoryExternalId = "R_kwDO_repo",
            RepositoryOwner = "joelfuller2016",
            RepositoryName = "KanbanFlow-v2",
            Title = "Frozen title",
            Body = "Frozen body",
            RequestHash = new string('a', 64),
            ExpectedTaskUpdatedAt = storedUtcTicks,
            LeaseExpiresAt = storedUtcTicks.AddSeconds(15),
            LastAttemptAt = storedUtcTicks,
            DispatchedAt = storedUtcTicks,
            CreatedAt = storedUtcTicks,
            UpdatedAt = storedUtcTicks,
            CompletedAt = storedUtcTicks
        };

        var response = ApiEndpoints.MapExternalWriteOperation(operation);

        Assert.Equal(DateTimeKind.Utc, response.ExpectedTaskUpdatedAt.Kind);
        Assert.Equal(DateTimeKind.Utc, response.LeaseExpiresAt!.Value.Kind);
        Assert.Equal(DateTimeKind.Utc, response.LastAttemptAt!.Value.Kind);
        Assert.Equal(DateTimeKind.Utc, response.DispatchedAt!.Value.Kind);
        Assert.Equal(DateTimeKind.Utc, response.CreatedAt.Kind);
        Assert.Equal(DateTimeKind.Utc, response.UpdatedAt.Kind);
        Assert.Equal(DateTimeKind.Utc, response.CompletedAt!.Value.Kind);

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(
            response,
            new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        Assert.EndsWith("Z", json.RootElement.GetProperty("leaseExpiresAt").GetString());
    }

    [Theory]
    [InlineData(typeof(ValidationException), StatusCodes.Status422UnprocessableEntity)]
    [InlineData(typeof(TaskNotFoundException), StatusCodes.Status404NotFound)]
    [InlineData(typeof(ExternalWriteOperationNotFoundException), StatusCodes.Status404NotFound)]
    [InlineData(typeof(ExternalWorkItemConflictException), StatusCodes.Status409Conflict)]
    public async Task EndpointExceptionBoundary_MapsStableHttpStatus(Type exceptionType, int expectedStatus)
    {
        Exception exception;
        if (exceptionType == typeof(ValidationException))
            exception = new ValidationException("invalid");
        else if (exceptionType == typeof(TaskNotFoundException))
            exception = new TaskNotFoundException(5);
        else if (exceptionType == typeof(ExternalWriteOperationNotFoundException))
            exception = new ExternalWriteOperationNotFoundException(Guid.NewGuid());
        else
            exception = new ExternalWorkItemConflictException("conflict", "already reserved");

        var result = await ApiEndpoints.ExecuteExternalWorkItemEndpointAsync(
            () => Task.FromException<IResult>(exception));

        var status = Assert.IsAssignableFrom<IStatusCodeHttpResult>(result);
        Assert.Equal(expectedStatus, status.StatusCode);
    }

    private static ExternalWorkItemLink Link(int id, int taskId, ExternalLinkState state) => new()
    {
        Id = id,
        TaskId = taskId,
        Provider = ExternalProvider.GitHub,
        State = state,
        RepositoryExternalId = "R_kwDO_repo",
        RepositoryOwner = "joelfuller2016",
        RepositoryName = "KanbanFlow-v2",
        ExternalNodeId = "I_kwDO_issue",
        ExternalNumber = 336,
        Url = "https://github.com/joelfuller2016/KanbanFlow-v2/issues/336"
    };
}
