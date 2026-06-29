using KanbanFlow.Models;
using KanbanFlow.Services;

namespace KanbanFlow.Tests;

/// <summary>
/// Real-code tests for <see cref="VectorSearch"/> — the brute-force cosine v1 (#222, retargeted by
/// the #249 spike) — against a live SQLite database (<see cref="SqliteTestFactory"/>). No mocks and no
/// network: embeddings are seeded DIRECTLY through <see cref="MemoryService.SetEmbeddingAsync"/> with
/// known vectors, then searched through the real service, so every test exercises the actual EF query
/// + the real cosine ranking. (The reindex REST route's live embed path needs ZeroRouter and is
/// smoke-tested manually; its core write — SetEmbeddingAsync — is covered in MemoryServiceTests.)
/// </summary>
public sealed class VectorSearchTests
{
    /// <summary>Create a current project-scoped node and attach <paramref name="vector"/> as its embedding.</summary>
    private static async Task<int> SeedEmbeddedNode(
        SqliteTestFactory f, MemoryService svc, string scopeKey, float[] vector, string content)
    {
        var node = await svc.CreateNodeAsync(
            new MemoryNodeCreate(MemoryType.Semantic, MemoryScope.Project, scopeKey, content), null);
        await svc.SetEmbeddingAsync(node.Id, vector, null);
        return node.Id;
    }

    [Fact]
    public async Task Search_OrdersByCosineSimilarity_DescendingNearestFirst()
    {
        using var f = new SqliteTestFactory();
        var svc = new MemoryService(f);

        // Query [1,0,0]. Cosines: A=[1,0,0]→1.0, B=[2,1,0]→0.894, C=[1,2,0]→0.447 — strictly
        // decreasing, so the nearest (A) must rank first and the scores must be monotonic.
        var idA = await SeedEmbeddedNode(f, svc, "1", new[] { 1f, 0f, 0f }, "A");
        var idB = await SeedEmbeddedNode(f, svc, "1", new[] { 2f, 1f, 0f }, "B");
        var idC = await SeedEmbeddedNode(f, svc, "1", new[] { 1f, 2f, 0f }, "C");

        var search = new VectorSearch(f);
        var hits = await search.SearchAsync(MemoryScope.Project, "1", new[] { 1f, 0f, 0f }, 10);

        Assert.Equal(3, hits.Count);
        Assert.Equal(idA, hits[0].Id);                       // nearest first
        Assert.True(Math.Abs(hits[0].Score - 1.0) < 1e-6);   // identical direction ⇒ cosine 1
        Assert.Equal(new[] { idA, idB, idC }, hits.Select(h => h.Id).ToArray());

        // Scores are monotonic non-increasing.
        for (var i = 1; i < hits.Count; i++)
            Assert.True(hits[i - 1].Score >= hits[i].Score, $"score[{i - 1}] < score[{i}]");
    }

    [Fact]
    public async Task Search_ScopeIsolation_ExcludesOtherScope()
    {
        using var f = new SqliteTestFactory();
        var svc = new MemoryService(f);

        var inScope = await SeedEmbeddedNode(f, svc, "1", new[] { 1f, 0f, 0f }, "scope-1");
        var otherScope = await SeedEmbeddedNode(f, svc, "2", new[] { 1f, 0f, 0f }, "scope-2");

        var search = new VectorSearch(f);
        var hits = await search.SearchAsync(MemoryScope.Project, "1", new[] { 1f, 0f, 0f }, 10);

        var ids = hits.Select(h => h.Id).ToList();
        Assert.Contains(inScope, ids);
        Assert.DoesNotContain(otherScope, ids); // scope "2" node never leaks into a scope "1" search
    }

    [Fact]
    public async Task Search_ExcludesSupersededNodes()
    {
        using var f = new SqliteTestFactory();
        var svc = new MemoryService(f);

        var retired = await SeedEmbeddedNode(f, svc, "1", new[] { 1f, 0f, 0f }, "retire me");
        var current = await SeedEmbeddedNode(f, svc, "1", new[] { 1f, 0f, 0f }, "keep me");
        await svc.SupersedeAsync(retired, null); // stamps ValidTo — now historical

        var search = new VectorSearch(f);
        var hits = await search.SearchAsync(MemoryScope.Project, "1", new[] { 1f, 0f, 0f }, 10);

        var ids = hits.Select(h => h.Id).ToList();
        Assert.Contains(current, ids);
        Assert.DoesNotContain(retired, ids); // a superseded node is never a search hit
    }

    [Fact]
    public async Task Search_SkipsDimensionMismatch()
    {
        using var f = new SqliteTestFactory();
        var svc = new MemoryService(f);

        var dim4 = await SeedEmbeddedNode(f, svc, "1", new[] { 1f, 0f, 0f, 0f }, "4-dim");
        var dim3 = await SeedEmbeddedNode(f, svc, "1", new[] { 1f, 0f, 0f }, "3-dim");

        var search = new VectorSearch(f);
        // A length-3 query: the length-4 candidate is skipped (no throw), the length-3 one matches.
        var hits = await search.SearchAsync(MemoryScope.Project, "1", new[] { 1f, 0f, 0f }, 10);

        var ids = hits.Select(h => h.Id).ToList();
        Assert.Contains(dim3, ids);
        Assert.DoesNotContain(dim4, ids);
    }

    [Fact]
    public async Task Search_EmptyCorpus_ReturnsEmpty()
    {
        using var f = new SqliteTestFactory();
        var search = new VectorSearch(f);

        var hits = await search.SearchAsync(MemoryScope.Project, "1", new[] { 1f, 0f, 0f }, 10);
        Assert.Empty(hits);
    }

    [Fact]
    public async Task Search_NoEmbeddings_ReturnsEmpty()
    {
        using var f = new SqliteTestFactory();
        var svc = new MemoryService(f);

        // Current nodes exist but were never embedded (EmbeddingBlob == null) ⇒ nothing to rank.
        await svc.CreateNodeAsync(new MemoryNodeCreate(MemoryType.Semantic, MemoryScope.Project, "1", "unembedded a"), null);
        await svc.CreateNodeAsync(new MemoryNodeCreate(MemoryType.Semantic, MemoryScope.Project, "1", "unembedded b"), null);

        var search = new VectorSearch(f);
        var hits = await search.SearchAsync(MemoryScope.Project, "1", new[] { 1f, 0f, 0f }, 10);
        Assert.Empty(hits);
    }

    [Fact]
    public async Task Search_RespectsTopK()
    {
        using var f = new SqliteTestFactory();
        var svc = new MemoryService(f);

        var idA = await SeedEmbeddedNode(f, svc, "1", new[] { 1f, 0f, 0f }, "A"); // cosine 1.0
        await SeedEmbeddedNode(f, svc, "1", new[] { 1f, 1f, 0f }, "B");           // cosine 0.707
        await SeedEmbeddedNode(f, svc, "1", new[] { 0f, 1f, 0f }, "C");           // cosine 0.0

        var search = new VectorSearch(f);
        var hits = await search.SearchAsync(MemoryScope.Project, "1", new[] { 1f, 0f, 0f }, 2);

        Assert.Equal(2, hits.Count);    // capped at k even though 3 candidates match
        Assert.Equal(idA, hits[0].Id);  // and it returns the top-k, not an arbitrary 2
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task Search_NonPositiveK_Throws(int k)
    {
        using var f = new SqliteTestFactory();
        var search = new VectorSearch(f);
        await Assert.ThrowsAsync<ValidationException>(
            () => search.SearchAsync(MemoryScope.Project, "1", new[] { 1f, 0f, 0f }, k));
    }

    [Fact]
    public async Task Search_EmptyQuery_Throws()
    {
        using var f = new SqliteTestFactory();
        var search = new VectorSearch(f);
        await Assert.ThrowsAsync<ValidationException>(
            () => search.SearchAsync(MemoryScope.Project, "1", Array.Empty<float>(), 5));
    }

    [Fact]
    public void IsAvailable_IsTrue()
    {
        // Documents the #223 fusion contract: the brute-force backend has no native dependency.
        using var f = new SqliteTestFactory();
        Assert.True(new VectorSearch(f).IsAvailable);
    }

    [Fact]
    public async Task Search_ZeroNormQuery_ReturnsZeroScores()
    {
        // CosineSimilarity returns 0 when normA == 0 (all-zero query); search must not produce NaN
        // and must return a valid zero-scored result rather than crashing or skipping silently.
        using var f = new SqliteTestFactory();
        var svc = new MemoryService(f);
        await SeedEmbeddedNode(f, svc, "1", new[] { 1f, 0f, 0f }, "A");

        var search = new VectorSearch(f);
        var hits = await search.SearchAsync(MemoryScope.Project, "1", new[] { 0f, 0f, 0f }, 5);

        Assert.Single(hits);
        Assert.Equal(0.0, hits[0].Score);
    }

    [Fact]
    public async Task Search_ZeroNormStoredEmbedding_ReturnsZeroScore()
    {
        // A stored all-zero embedding is a valid (though degenerate) input — search must return
        // score 0, not crash or produce NaN.
        using var f = new SqliteTestFactory();
        var svc = new MemoryService(f);
        await SeedEmbeddedNode(f, svc, "1", new[] { 0f, 0f, 0f }, "zero-vec");

        var search = new VectorSearch(f);
        var hits = await search.SearchAsync(MemoryScope.Project, "1", new[] { 1f, 0f, 0f }, 5);

        Assert.Single(hits);
        Assert.Equal(0.0, hits[0].Score);
    }

    [Fact]
    public async Task Search_TieBreak_DeterministicById()
    {
        // Two nodes with identical embeddings tie on cosine; the result order must be a stable
        // ascending-Id sort (not DB-materialisation order), and identical across repeated calls.
        using var f = new SqliteTestFactory();
        var svc = new MemoryService(f);
        var id1 = await SeedEmbeddedNode(f, svc, "1", new[] { 1f, 1f, 0f }, "tie-A");
        var id2 = await SeedEmbeddedNode(f, svc, "1", new[] { 1f, 1f, 0f }, "tie-B");

        var search = new VectorSearch(f);
        var first = await search.SearchAsync(MemoryScope.Project, "1", new[] { 1f, 1f, 0f }, 10);
        var second = await search.SearchAsync(MemoryScope.Project, "1", new[] { 1f, 1f, 0f }, 10);

        Assert.Equal(new[] { id1, id2 }, first.Select(h => h.Id).ToArray());  // ascending-Id tie order
        Assert.Equal(first[0].Score, first[1].Score);                          // genuine tie
        Assert.Equal(first.Select(h => h.Id), second.Select(h => h.Id));       // stable across calls
    }

    [Fact]
    public async Task Search_SkipsMalformedBlob_WithoutCrashing()
    {
        // A corrupt/partial EmbeddingBlob (length not a multiple of 4) must be skipped, not crash the
        // whole search. Insert one directly (bypassing SetEmbeddingAsync, which only writes valid
        // float32) alongside one validly-embedded node.
        using var f = new SqliteTestFactory();
        var svc = new MemoryService(f);
        var validId = await SeedEmbeddedNode(f, svc, "1", new[] { 1f, 0f, 0f }, "valid");

        await using (var db = await f.CreateDbContextAsync())
        {
            db.MemoryNodes.Add(new MemoryNode
            {
                Type = MemoryType.Semantic,
                Scope = MemoryScope.Project,
                ScopeKey = "1",
                Content = "malformed-blob",
                EmbeddingBlob = new byte[] { 1, 2, 3 }, // length 3 — not a whole number of float32s
                EmbeddingDim = 0
            });
            await db.SaveChangesAsync();
        }

        var search = new VectorSearch(f);
        var hits = await search.SearchAsync(MemoryScope.Project, "1", new[] { 1f, 0f, 0f }, 10);

        Assert.Single(hits);                 // only the valid node; the malformed blob is skipped
        Assert.Equal(validId, hits[0].Id);
    }
}
