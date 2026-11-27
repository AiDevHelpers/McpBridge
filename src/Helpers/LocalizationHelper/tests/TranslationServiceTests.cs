using System.Text.Json;
using Xunit;

namespace AiDevHelpers.Helpers.LocalizationHelper.Tests;

/// <summary>A translator that answers from a function over the keys of each batch, and counts its calls.</summary>
internal sealed class FakeTranslator(Func<IReadOnlyList<string>, string> reply) : ITranslator
{
    public int Calls { get; private set; }

    public List<IReadOnlyList<string>> Batches { get; } = [];

    public Task<string> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken ct)
    {
        Calls++;
        using var doc = JsonDocument.Parse(userPrompt[userPrompt.IndexOf('{')..]);
        var keys = doc.RootElement.EnumerateObject().Select(p => p.Name).ToList();
        Batches.Add(keys);
        return Task.FromResult(reply(keys));
    }

    public static string Json(IEnumerable<string> keys, Func<string, string> translate) =>
        JsonSerializer.Serialize(keys.ToDictionary(k => k, translate));
}

public sealed class TranslationServiceTests : IDisposable
{
    private readonly ResxFixture _fx = new();
    private readonly PlanStore<TranslatePlan> _store = new();

    public void Dispose() => _fx.Dispose();

    private static (string, string, string?) E(string n, string v, string? c = null) => (n, v, c);

    private string Culture(params (string, string, string?)[] de)
    {
        _fx.Resx("P", null, "\n", false, E("A", "Hello {0}"), E("B", "Goodbye"), E("C", "Thanks"));
        return _fx.Resx("P", "de", "\n", false, de);
    }

    private static (string, string, string?) Stub(string k, string v) => (k, v, CultureSyncChecker.NeedsTranslation);

    private TranslationService Service(FakeTranslator fake, int batchSize = 20) => new(fake, _store, batchSize);

    private static FakeTranslator Good() => new(keys => FakeTranslator.Json(keys, k => k == "A" ? "Hallo {0}" : "de-" + k));

    [Fact]
    public void PlanListsOnlyNeedsTranslationKeys()
    {
        Culture(Stub("A", "Hello {0}"), ("B", "Auf Wiedersehen", null), Stub("C", "Thanks"));

        var plan = Service(Good()).Plan(_fx.Root, "de", null);

        Assert.Equal(["A", "C"], plan.Items.Select(i => i.Key));
    }

    [Fact]
    public void PlanSendsNothing()
    {
        Culture(Stub("A", "Hello {0}"));
        var fake = Good();

        Service(fake).Plan(_fx.Root, "de", null);

        Assert.Equal(0, fake.Calls);
    }

    [Fact]
    public void KeysNarrowThePlan()
    {
        Culture(Stub("A", "Hello {0}"), Stub("B", "Goodbye"));

        var plan = Service(Good()).Plan(_fx.Root, "de", ["B"]);

        Assert.Equal("B", Assert.Single(plan.Items).Key);
    }

    [Fact]
    public void NonStubKeyRefused()
    {
        Culture(Stub("A", "Hello {0}"), ("B", "Auf Wiedersehen", null));

        Assert.Throws<ArgumentException>(() => Service(Good()).Plan(_fx.Root, "de", ["B"]));
    }

    [Fact]
    public async Task ApplyWritesValidTranslationWithMachineTranslatedComment()
    {
        var de = Culture(Stub("A", "Hello {0}"));
        var service = Service(Good());

        var result = await service.ApplyAsync(service.Plan(_fx.Root, "de", null).Token, default);

        var entry = ResxFile.Load(de).Entries.Single(e => e.Name == "A");
        Assert.Equal(("Hallo {0}", "machine-translated"), (entry.Value, entry.Comment));
        Assert.Equal("A", Assert.Single(result.Written));
        Assert.Empty(result.Rejected);
    }

    [Fact]
    public async Task PlaceholderChangingReplyIsRejectedAndLeftAsStub()
    {
        var de = Culture(Stub("A", "Hello {0}"));
        var service = Service(new FakeTranslator(keys => FakeTranslator.Json(keys, _ => "Hallo")));

        var result = await service.ApplyAsync(service.Plan(_fx.Root, "de", null).Token, default);

        Assert.Equal(("A", "placeholder-mismatch"), (result.Rejected[0].Key, result.Rejected[0].Reason));
        Assert.Equal(CultureSyncChecker.NeedsTranslation, ResxFile.Load(de).Entries.Single().Comment);
    }

    [Fact]
    public async Task EmptyReplyRejected()
    {
        Culture(Stub("B", "Goodbye"));
        var service = Service(new FakeTranslator(keys => FakeTranslator.Json(keys, _ => "  ")));

        var result = await service.ApplyAsync(service.Plan(_fx.Root, "de", null).Token, default);

        Assert.Equal("empty", Assert.Single(result.Rejected).Reason);
    }

    [Fact]
    public async Task MissingKeyInReplyRejected()
    {
        Culture(Stub("B", "Goodbye"), Stub("C", "Thanks"));
        var service = Service(new FakeTranslator(_ => "{\"B\":\"Tschuess\"}"));

        var result = await service.ApplyAsync(service.Plan(_fx.Root, "de", null).Token, default);

        Assert.Equal(("C", "missing"), (result.Rejected.Single().Key, result.Rejected.Single().Reason));
    }

    [Fact]
    public async Task ExtraKeyInReplyIgnored()
    {
        var de = Culture(Stub("B", "Goodbye"));
        var service = Service(new FakeTranslator(_ => "{\"B\":\"Tschuess\",\"Z\":\"extra\"}"));

        await service.ApplyAsync(service.Plan(_fx.Root, "de", null).Token, default);

        Assert.DoesNotContain(ResxFile.Load(de).Entries, e => e.Name == "Z");
    }

    [Fact]
    public async Task UnparseableReplyRejectsTheBatch()
    {
        Culture(Stub("B", "Goodbye"), Stub("C", "Thanks"));
        var service = Service(new FakeTranslator(_ => "not json"));

        var result = await service.ApplyAsync(service.Plan(_fx.Root, "de", null).Token, default);

        Assert.All(result.Rejected, r => Assert.Equal("unparseable-reply", r.Reason));
        Assert.Equal(2, result.Rejected.Count);
    }

    [Fact]
    public async Task LineBreakIsRejectedUnlessTheSourceHasOne()
    {
        Culture(Stub("B", "Goodbye"));
        var service = Service(new FakeTranslator(keys => FakeTranslator.Json(keys, _ => "a\nb")));

        var result = await service.ApplyAsync(service.Plan(_fx.Root, "de", null).Token, default);

        Assert.Equal("unexpected-line-break", Assert.Single(result.Rejected).Reason);
    }

    [Fact]
    public async Task BatchesSplitAtTheCap()
    {
        var entries = Enumerable.Range(1, 45).Select(n => Stub("K" + n.ToString("00"), "Text " + n)).ToArray();
        _fx.Resx("P", null, "\n", false, entries.Select(e => (e.Item1, e.Item2, (string?)null)).ToArray());
        _fx.Resx("P", "de", "\n", false, entries);
        var fake = new FakeTranslator(keys => FakeTranslator.Json(keys, k => "de-" + k));
        var service = Service(fake);

        var plan = service.Plan(_fx.Root, "de", null);
        await service.ApplyAsync(plan.Token, default);

        Assert.Equal(3, plan.Batches);
        Assert.Equal([20, 20, 5], fake.Batches.Select(b => b.Count));
    }

    [Fact]
    public async Task FailedBatchDoesNotStopOthers()
    {
        var entries = Enumerable.Range(1, 25).Select(n => Stub("K" + n.ToString("00"), "Text " + n)).ToArray();
        _fx.Resx("P", null, "\n", false, entries.Select(e => (e.Item1, e.Item2, (string?)null)).ToArray());
        var de = _fx.Resx("P", "de", "\n", false, entries);
        var calls = 0;
        var translator = new ThrowingFirstTranslator(() => calls++);
        var service = new TranslationService(translator, _store, 20);

        var result = await service.ApplyAsync(service.Plan(_fx.Root, "de", null).Token, default);

        Assert.Equal(20, result.Rejected.Count(r => r.Reason == "translator-failed"));
        Assert.Equal(5, result.Written.Count);
        Assert.Equal(5, ResxFile.Load(de).Entries.Count(e => e.Comment == "machine-translated"));
    }

    [Fact]
    public async Task OnlyPlannedKeysWritten()
    {
        var de = Culture(Stub("B", "Goodbye"), ("C", "Danke", null));
        var service = Service(new FakeTranslator(_ => "{\"B\":\"Tschuess\",\"C\":\"Changed\"}"));

        await service.ApplyAsync(service.Plan(_fx.Root, "de", null).Token, default);

        Assert.Equal("Danke", ResxFile.Load(de).Entries.Single(e => e.Name == "C").Value);
    }

    [Fact]
    public async Task ConfirmRefusesWhenFileChanged()
    {
        var de = Culture(Stub("B", "Goodbye"));
        var service = Service(Good());
        var token = service.Plan(_fx.Root, "de", null).Token;

        File.AppendAllText(de, "\n");

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ApplyAsync(token, default));
    }

    [Fact]
    public async Task TokenSingleUse()
    {
        Culture(Stub("B", "Goodbye"));
        var service = Service(Good());
        var token = service.Plan(_fx.Root, "de", null).Token;

        await service.ApplyAsync(token, default);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ApplyAsync(token, default));
    }

    [Fact]
    public async Task AfterApplySyncCheckNoLongerReportsThoseKeysUntranslated()
    {
        Culture(Stub("A", "Hello {0}"), Stub("B", "Goodbye"), Stub("C", "Thanks"));
        var service = Service(Good());

        await service.ApplyAsync(service.Plan(_fx.Root, "de", null).Token, default);

        Assert.Empty(CultureSyncChecker.Check(_fx.Root));
    }

    private sealed class ThrowingFirstTranslator(Action onCall) : ITranslator
    {
        private int _calls;

        public Task<string> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken ct)
        {
            onCall();
            if (_calls++ == 0)
            {
                throw new InvalidOperationException("boom");
            }

            using var doc = JsonDocument.Parse(userPrompt[userPrompt.IndexOf('{')..]);
            return Task.FromResult(FakeTranslator.Json(doc.RootElement.EnumerateObject().Select(p => p.Name), k => "de-" + k));
        }
    }
}
