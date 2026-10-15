using System.Reflection;
using System.Text.Json;
using Collaborator.Agents;
using Collaborator.Core;

namespace Collaborator.Tests;

[TestClass]
public sealed class DeveloperSkillDraftTests
{
    [TestMethod]
    public void ResponseSchemas_OmitProviderUnsupportedLengthAndCountConstraints()
    {
        var schemas = typeof(CopilotPlanGenerator).GetFields(BindingFlags.NonPublic | BindingFlags.Static)
            .Where(field => field.FieldType == typeof(JsonElement));

        foreach (var field in schemas)
        {
            var schema = (JsonElement)field.GetValue(null)!;
            foreach (var keyword in new[] { "minItems", "maxItems", "minLength", "maxLength" })
            {
                Assert.DoesNotContain($"\"{keyword}\"", schema.GetRawText(),
                    $"{field.Name} must leave {keyword} validation to InputRules.");
            }
        }
    }

    [TestMethod]
    public void ValidateDeveloperSkillDraft_AcceptsExactLimits()
    {
        var draft = new DeveloperSkillDraft(
            new string('s', 4000),
            Enumerable.Repeat(new string('c', 2000), 30).ToArray(),
            Enumerable.Repeat(new string('r', 1000), 30).ToArray(),
            Enumerable.Repeat(new string('v', 1000), 30).ToArray(),
            Enumerable.Repeat(new string('q', 1000), 30).ToArray());

        InputRules.ValidateDeveloperSkillDraft(draft);
        InputRules.ValidateDeveloperSkillDraft(new DeveloperSkillDraft("Summary", ["Change"], [], [], []));
    }

    [TestMethod]
    [DataRow("summary")]
    [DataRow("proposedChanges")]
    [DataRow("risks")]
    [DataRow("verificationNeeds")]
    [DataRow("openQuestions")]
    public void ValidateDeveloperSkillDraft_RejectsOversizedFieldWithItsName(string field)
    {
        var draft = new DeveloperSkillDraft("Summary", ["Change"], [], [], []);
        draft = field switch
        {
            "summary" => draft with { Summary = new string('s', 4001) },
            "proposedChanges" => draft with { ProposedChanges = [new string('c', 2001)] },
            "risks" => draft with { Risks = [new string('r', 1001)] },
            "verificationNeeds" => draft with { VerificationNeeds = [new string('v', 1001)] },
            "openQuestions" => draft with { OpenQuestions = [new string('q', 1001)] },
            _ => throw new ArgumentOutOfRangeException(nameof(field))
        };

        var error = Assert.ThrowsExactly<InvalidDataException>(() =>
            InputRules.ValidateDeveloperSkillDraft(draft));

        Assert.Contains(field, error.Message);
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(31)]
    public void ValidateDeveloperSkillDraft_RejectsInvalidProposedChangeCount(int count)
    {
        var draft = new DeveloperSkillDraft(
            "Summary", Enumerable.Repeat("Change", count).ToArray(), [], [], []);

        var error = Assert.ThrowsExactly<InvalidDataException>(() =>
            InputRules.ValidateDeveloperSkillDraft(draft));

        Assert.Contains("proposedChanges", error.Message);
    }
}
