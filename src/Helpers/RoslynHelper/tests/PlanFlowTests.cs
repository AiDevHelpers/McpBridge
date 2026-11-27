using System.Text.Json;
using AiDevHelpers.Installers.Vs2026.Core;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Xunit;

namespace AiDevHelpers.Helpers.RoslynHelper.Tests;

// The shared plan flow: plan, refusals, diffs, hash, then apply. The reply shape must match the VS bridge's reply.
public class PlanFlowTests
{
    private static async Task<(Solution Solution, DocumentId Id)> LoadGood(FixtureCopy fixture)
    {
        var loaded = await new SolutionLoader().LoadAsync(fixture.SolutionPath);
        var document = loaded.Solution.Projects.Single(p => p.Name == "Good").Documents.Single(d => d.Name == "Class1.cs");
        return (loaded.Solution, document.Id);
    }

    private static Func<Solution, Task<MethodEditPlan>> ChangeClass1(DocumentId id) => async snapshot =>
    {
        var text = await snapshot.GetDocument(id)!.GetTextAsync();
        var plan = new MethodEditPlan { Result = snapshot.WithDocumentText(id, SourceText.From("// planned\n" + text)) };
        plan.Files.Add(snapshot.GetDocument(id)!.FilePath!);
        return plan;
    };

    private static JsonElement ToJson(object reply) => JsonSerializer.SerializeToElement(reply);

    [Fact]
    public async Task unconfirmed_plan_reply_has_planned_true_and_applied_false()
    {
        using var fixture = FixtureCopy.Create();
        var (solution, id) = await LoadGood(fixture);
        var applied = false;

        var reply = ToJson(await PlanFlow.RunAsync(
            solution, ChangeClass1(id), confirm: false, expectedHash: null,
            apply: _ => { applied = true; return Task.CompletedTask; },
            afterApply: _ => (new List<object>(), new List<object>())));

        Assert.True(reply.GetProperty("planned").GetBoolean());
        Assert.False(reply.GetProperty("applied").GetBoolean());
        Assert.True(reply.TryGetProperty("check", out _));
        Assert.False(applied);
    }

    [Fact]
    public async Task confirmed_plan_applies_and_reports_moved_and_deleted()
    {
        using var fixture = FixtureCopy.Create();
        var (solution, id) = await LoadGood(fixture);
        var applied = false;

        var reply = ToJson(await PlanFlow.RunAsync(
            solution, ChangeClass1(id), confirm: true, expectedHash: null,
            apply: _ => { applied = true; return Task.CompletedTask; },
            afterApply: _ => (new List<object> { "moved" }, new List<object> { "deleted" })));

        Assert.True(applied);
        Assert.False(reply.GetProperty("planned").GetBoolean());
        Assert.True(reply.GetProperty("applied").GetBoolean());
        Assert.Equal("moved", reply.GetProperty("moved")[0].GetString());
        Assert.Equal("deleted", reply.GetProperty("deleted")[0].GetString());
    }

    [Fact]
    public async Task refused_plan_is_not_applied_even_when_confirmed()
    {
        using var fixture = FixtureCopy.Create();
        var (solution, _) = await LoadGood(fixture);
        var applied = false;
        Func<Solution, Task<MethodEditPlan>> refused = _ =>
        {
            var plan = new MethodEditPlan();
            plan.Reasons.Add("refused for the test");
            return Task.FromResult(plan);
        };

        var reply = ToJson(await PlanFlow.RunAsync(
            solution, refused, confirm: true, expectedHash: null,
            apply: _ => { applied = true; return Task.CompletedTask; },
            afterApply: _ => (new List<object>(), new List<object>())));

        Assert.False(applied);
        Assert.False(reply.GetProperty("planned").GetBoolean());
        Assert.False(reply.GetProperty("applied").GetBoolean());
    }
}
