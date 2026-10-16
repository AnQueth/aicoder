using Collaborator.Core;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace Collaborator.Infrastructure;

/// <summary>Idempotent activity persistence and revision-bound decisions.</summary>
public sealed partial class RunStore(CollaboratorDb db)
{
    public async Task<FeatureRun> GetAsync(Guid id, CancellationToken cancellationToken) =>
        await db.Runs.SingleAsync(x => x.Id == id, cancellationToken);

    public async Task<PlanRevisionRecord?> GetRevisionAsync(Guid id, int revision, CancellationToken cancellationToken) =>
        await db.PlanRevisions.AsNoTracking()
            .SingleOrDefaultAsync(x => x.RunId == id && x.Revision == revision, cancellationToken);

    public async Task<FeatureSpecificationRevisionRecord?> GetFeatureSpecificationRevisionAsync(
        Guid id, int revision, CancellationToken cancellationToken) =>
        await db.FeatureSpecificationRevisions.AsNoTracking()
            .SingleOrDefaultAsync(x => x.RunId == id && x.Revision == revision, cancellationToken);

    public Task<WorkflowArtifactRecord?> GetCurrentArchitectureArtifactAsync(
        Guid id, CancellationToken cancellationToken) =>
        db.WorkflowArtifacts.AsNoTracking()
            .Where(x => x.RunId == id && x.Stage == WorkflowStages.Architecture &&
                x.ArtifactKey == "architecture-analysis")
            .OrderByDescending(x => x.Revision)
            .FirstOrDefaultAsync(cancellationToken);

    public async Task<(AgentSkillRevisionReference Reference, string Name, string Prompt)?>
        GetActiveAgentSkillAsync(
            string roleId, string domain, string skillId, CancellationToken cancellationToken)
    {
        var skill = await db.AgentSkills.AsNoTracking().SingleOrDefaultAsync(
            x => x.RoleId == roleId && x.Domain == domain && x.SkillId == skillId && x.IsEnabled,
            cancellationToken);
        if (skill is null)
        {
            return null;
        }

        var revision = await db.AgentSkillRevisions.AsNoTracking().SingleOrDefaultAsync(
            x => x.RoleId == roleId && x.Domain == domain && x.SkillId == skillId &&
                x.Revision == skill.ActiveRevision,
            cancellationToken);
        return revision is null
            ? null
            : (new AgentSkillRevisionReference(
                    revision.RoleId, revision.SkillId, revision.Domain, revision.Revision),
                revision.Name, revision.Prompt);
    }

    public async Task<DeveloperSkillSnapshot[]> GetDeveloperSkillSnapshotsAsync(
        Guid id, CancellationToken cancellationToken)
    {
        var serialized = await db.Runs.AsNoTracking().Where(x => x.Id == id)
            .Select(x => x.DeveloperSkillsJson).SingleAsync(cancellationToken);
        var snapshots = JsonSerializer.Deserialize<DeveloperSkillSnapshot[]>(
            serialized, JsonSerializerOptions.Web)
            ?? throw new InvalidDataException("Persisted developer skill snapshots are invalid.");
        if (snapshots.Any(snapshot => snapshot is null || snapshot.Skill is null ||
                !InputRules.IsProductId(snapshot.Skill.RoleId) ||
                !snapshot.Skill.RoleId.StartsWith("developer-", StringComparison.Ordinal) ||
                !InputRules.IsProductId(snapshot.Skill.Domain) ||
                !InputRules.IsProductId(snapshot.Skill.SkillId) ||
                snapshot.Skill.Revision < 1 ||
                !InputRules.IsText(snapshot.Name, 200) ||
                !InputRules.IsText(snapshot.Prompt, InputRules.MaximumAgentSkillPromptLength)) ||
            snapshots.Select(snapshot => (
                    snapshot.Skill.RoleId, snapshot.Skill.Domain, snapshot.Skill.SkillId))
                .Distinct().Count() != snapshots.Length)
        {
            throw new InvalidDataException("A run must contain distinct, enabled developer skill snapshots.");
        }
        return snapshots;
    }

    public async Task<DeveloperSkillSnapshot> GetDeveloperSkillSnapshotAsync(
        Guid id, AgentSkillRevisionReference reference, CancellationToken cancellationToken)
    {
        var snapshots = await GetDeveloperSkillSnapshotsAsync(id, cancellationToken);
        return snapshots.SingleOrDefault(snapshot =>
                snapshot.Skill.RoleId == reference.RoleId &&
                snapshot.Skill.Domain == reference.Domain &&
                snapshot.Skill.SkillId == reference.SkillId &&
                snapshot.Skill.Revision == reference.Revision)
            ?? throw new InvalidOperationException(
                "The requested developer skill revision is not part of this run's snapshot.");
    }

    public async Task<bool> HasDeveloperSkillAnalysisAsync(
        Guid id, AgentSkillRevisionReference reference, CancellationToken cancellationToken)
    {
        var snapshot = await GetDeveloperSkillSnapshotAsync(id, reference, cancellationToken);
        var artifactKey = GetDeveloperSkillArtifactKey(snapshot.Skill);
        return await db.WorkflowArtifacts.AsNoTracking().AnyAsync(
            artifact => artifact.RunId == id &&
                artifact.Stage == WorkflowStages.SpecialistAnalysis &&
                artifact.ArtifactKey == artifactKey &&
                artifact.Revision == 1 &&
                artifact.Status == WorkflowArtifactStatuses.Completed,
            cancellationToken);
    }

    public async Task StoreDeveloperSkillAnalysisAsync(
        Guid id,
        DeveloperSkillAnalysis analysis,
        string repositoryCommit,
        CancellationToken cancellationToken)
    {
        var run = await GetAsync(id, cancellationToken);
        var architecture = await GetCurrentArchitectureArtifactAsync(id, cancellationToken);
        if (!run.BusinessAnalystApproved ||
            architecture?.Status != WorkflowArtifactStatuses.Approved ||
            architecture.Decision != ArchitectureAnalysisDecisionKinds.Approve ||
            run.RepositoryCommit != repositoryCommit ||
            architecture.RepositoryCommit != repositoryCommit)
        {
            throw new InvalidOperationException(
                "Developer skill analysis requires the approved specification and architecture from the pinned repository commit.");
        }
        var snapshot = await GetDeveloperSkillSnapshotAsync(
            id,
            new AgentSkillRevisionReference(
                analysis.RoleId, analysis.SkillId, analysis.Domain, analysis.SkillRevision),
            cancellationToken);
        if (analysis.SkillName != snapshot.Name)
        {
            throw new InvalidDataException("Developer analysis skill name does not match its captured revision.");
        }
        var validated = InputRules.CreateDeveloperSkillAnalysis(snapshot, new DeveloperSkillDraft(
            analysis.Summary, analysis.ProposedChanges, analysis.Risks,
            analysis.VerificationNeeds, analysis.OpenQuestions));
        var artifactKey = GetDeveloperSkillArtifactKey(snapshot.Skill);
        var existing = await db.WorkflowArtifacts.SingleOrDefaultAsync(
            artifact => artifact.RunId == id &&
                artifact.Stage == WorkflowStages.SpecialistAnalysis &&
                artifact.ArtifactKey == artifactKey &&
                artifact.Revision == 1,
            cancellationToken);
        if (existing is not null)
        {
            return;
        }
        var specification = await db.FeatureSpecificationRevisions.AsNoTracking()
            .SingleOrDefaultAsync(
                revision => revision.RunId == id &&
                    revision.Revision == run.FeatureSpecificationRevision,
                cancellationToken);
        if (specification?.Decision != BusinessAnalystDecisionKinds.Approve)
        {
            throw new InvalidOperationException("Developer skill analysis requires the persisted approved specification.");
        }

        db.WorkflowArtifacts.Add(new WorkflowArtifactRecord
        {
            RunId = id,
            Stage = WorkflowStages.SpecialistAnalysis,
            ArtifactKey = artifactKey,
            Revision = 1,
            ArtifactType = nameof(DeveloperSkillAnalysis),
            PayloadJson = JsonSerializer.Serialize(validated, JsonSerializerOptions.Web),
            InputArtifactRevisionsJson = JsonSerializer.Serialize(
                new
                {
                    businessAnalysis = run.FeatureSpecificationRevision,
                    architecture = architecture.Revision
                },
                JsonSerializerOptions.Web),
            SkillRevisionsJson = JsonSerializer.Serialize(new[] { snapshot.Skill }, JsonSerializerOptions.Web),
            RepositoryCommit = repositoryCommit,
            Status = WorkflowArtifactStatuses.Completed
        });
        run.Status = "developer_skills_processing";
        AddEvent(id, "developer_skill_analysis_completed", snapshot.Skill.RoleId);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task CompleteDeveloperSkillAnalysesAsync(Guid id, CancellationToken cancellationToken)
    {
        var existingBundle = await db.WorkflowArtifacts.AnyAsync(
            artifact => artifact.RunId == id &&
                artifact.Stage == WorkflowStages.SpecialistAnalysis &&
                artifact.ArtifactKey == "developer-skills" &&
                artifact.Revision == 1 &&
                artifact.Status == WorkflowArtifactStatuses.Completed,
            cancellationToken);
        if (existingBundle)
        {
            return;
        }
        var run = await GetAsync(id, cancellationToken);
        var architecture = await GetCurrentArchitectureArtifactAsync(id, cancellationToken);
        if (!run.BusinessAnalystApproved ||
            architecture?.Status != WorkflowArtifactStatuses.Approved ||
            architecture.Decision != ArchitectureAnalysisDecisionKinds.Approve ||
            run.RepositoryCommit is null ||
            architecture.RepositoryCommit != run.RepositoryCommit)
        {
            throw new InvalidOperationException(
                "Developer analysis completion requires approved inputs from the pinned repository commit.");
        }
        var snapshots = await GetDeveloperSkillSnapshotsAsync(id, cancellationToken);
        var individualArtifacts = await db.WorkflowArtifacts.AsNoTracking()
            .Where(artifact => artifact.RunId == id &&
                artifact.Stage == WorkflowStages.SpecialistAnalysis &&
                artifact.ArtifactKey.StartsWith("developer-skill-") &&
                artifact.Revision == 1 &&
                artifact.Status == WorkflowArtifactStatuses.Completed)
            .ToListAsync(cancellationToken);
        var analyses = new Dictionary<(string RoleId, string Domain, string SkillId), DeveloperSkillAnalysis>();
        foreach (var artifact in individualArtifacts)
        {
            if (artifact.RepositoryCommit != run.RepositoryCommit)
            {
                throw new InvalidDataException("A developer skill result used a different repository commit.");
            }
            var references = JsonSerializer.Deserialize<AgentSkillRevisionReference[]>(
                artifact.SkillRevisionsJson, JsonSerializerOptions.Web) ?? [];
            var analysis = JsonSerializer.Deserialize<DeveloperSkillAnalysis>(
                artifact.PayloadJson, JsonSerializerOptions.Web)
                ?? throw new InvalidDataException("A persisted developer skill analysis is invalid.");
            if (references.Length != 1 ||
                references[0].RoleId != analysis.RoleId ||
                references[0].Domain != analysis.Domain ||
                references[0].SkillId != analysis.SkillId ||
                references[0].Revision != analysis.SkillRevision ||
                artifact.ArtifactKey != GetDeveloperSkillArtifactKey(references[0]) ||
                !analyses.TryAdd((analysis.RoleId, analysis.Domain, analysis.SkillId), analysis))
            {
                throw new InvalidDataException("Developer skill result provenance is invalid or duplicated.");
            }
        }
        if (analyses.Count != snapshots.Length ||
            snapshots.Any(snapshot =>
                !analyses.TryGetValue(
                    (snapshot.Skill.RoleId, snapshot.Skill.Domain, snapshot.Skill.SkillId),
                    out var analysis) ||
                analysis.SkillRevision != snapshot.Skill.Revision ||
                analysis.SkillName != snapshot.Name))
        {
            throw new InvalidOperationException(
                "Every captured developer skill must complete exactly one analysis before planning.");
        }

        var orderedAnalyses = snapshots.Select(snapshot =>
            analyses[(snapshot.Skill.RoleId, snapshot.Skill.Domain, snapshot.Skill.SkillId)]).ToArray();
        db.WorkflowArtifacts.Add(new WorkflowArtifactRecord
        {
            RunId = id,
            Stage = WorkflowStages.SpecialistAnalysis,
            ArtifactKey = "developer-skills",
            Revision = 1,
            ArtifactType = nameof(DeveloperSkillAnalysis),
            PayloadJson = JsonSerializer.Serialize(orderedAnalyses, JsonSerializerOptions.Web),
            InputArtifactRevisionsJson = JsonSerializer.Serialize(
                new
                {
                    businessAnalysis = run.FeatureSpecificationRevision,
                    architecture = architecture.Revision
                },
                JsonSerializerOptions.Web),
            SkillRevisionsJson = JsonSerializer.Serialize(
                snapshots.Select(snapshot => snapshot.Skill).ToArray(),
                JsonSerializerOptions.Web),
            RepositoryCommit = run.RepositoryCommit,
            Status = WorkflowArtifactStatuses.Completed
        });
        run.Status = "specialist_analysis_completed";
        AddEvent(id, "specialist_analysis_completed", "developer-skills");
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<DeveloperSkillAnalysis[]?> GetDeveloperSkillAnalysesAsync(
        Guid id, CancellationToken cancellationToken)
    {
        var artifact = await db.WorkflowArtifacts.AsNoTracking().SingleOrDefaultAsync(
            item => item.RunId == id &&
                item.Stage == WorkflowStages.SpecialistAnalysis &&
                item.ArtifactKey == "developer-skills" &&
                item.Revision == 1 &&
                item.Status == WorkflowArtifactStatuses.Completed,
            cancellationToken);
        return artifact is null
            ? null
            : JsonSerializer.Deserialize<DeveloperSkillAnalysis[]>(
                artifact.PayloadJson, JsonSerializerOptions.Web)
                ?? throw new InvalidDataException("Persisted developer skill analyses are invalid.");
    }

    public async Task StoreCollaborationSynthesisAsync(
        Guid id,
        CollaborationSynthesis synthesis,
        string repositoryCommit,
        CancellationToken cancellationToken)
    {
        var run = await GetAsync(id, cancellationToken);
        var architecture = await GetCurrentArchitectureArtifactAsync(id, cancellationToken);
        if (!run.BusinessAnalystApproved ||
            architecture?.Status != WorkflowArtifactStatuses.Approved ||
            architecture.Decision != ArchitectureAnalysisDecisionKinds.Approve ||
            run.RepositoryCommit != repositoryCommit ||
            architecture.RepositoryCommit != repositoryCommit)
        {
            throw new InvalidOperationException(
                "Collaboration synthesis requires the approved specification and architecture from the pinned repository commit.");
        }
        var specification = await GetFeatureSpecificationRevisionAsync(
            id, run.FeatureSpecificationRevision, cancellationToken);
        if (specification?.Decision != BusinessAnalystDecisionKinds.Approve)
        {
            throw new InvalidOperationException(
                "Collaboration synthesis requires the persisted approved feature specification.");
        }
        var snapshots = await GetDeveloperSkillSnapshotsAsync(id, cancellationToken);
        var capturedSkills = snapshots.Select(snapshot => snapshot.Skill).ToArray();
        var skillSet = capturedSkills.ToHashSet();
        InputRules.ValidateCollaborationSynthesis(synthesis, skillSet);
        var specialistAnalyses = await GetDeveloperSkillAnalysesAsync(id, cancellationToken);
        if (specialistAnalyses is null ||
            specialistAnalyses.Length != capturedSkills.Length)
        {
            throw new InvalidOperationException(
                "Collaboration synthesis requires a completed analysis from every captured developer skill.");
        }

        var existing = await db.WorkflowArtifacts.SingleOrDefaultAsync(
            artifact => artifact.RunId == id &&
                artifact.Stage == WorkflowStages.Synthesis &&
                artifact.ArtifactKey == "cross-role-synthesis" &&
                artifact.Revision == 1,
            cancellationToken);
        if (existing is not null)
        {
            return;
        }
        db.WorkflowArtifacts.Add(new WorkflowArtifactRecord
        {
            RunId = id,
            Stage = WorkflowStages.Synthesis,
            ArtifactKey = "cross-role-synthesis",
            Revision = 1,
            ArtifactType = nameof(CollaborationSynthesis),
            PayloadJson = JsonSerializer.Serialize(synthesis, JsonSerializerOptions.Web),
            InputArtifactRevisionsJson = JsonSerializer.Serialize(
                new
                {
                    businessAnalysis = run.FeatureSpecificationRevision,
                    architecture = architecture.Revision,
                    specialistAnalysis = 1
                },
                JsonSerializerOptions.Web),
            SkillRevisionsJson = JsonSerializer.Serialize(capturedSkills, JsonSerializerOptions.Web),
            RepositoryCommit = repositoryCommit,
            Status = WorkflowArtifactStatuses.Completed
        });
        run.Status = "collaboration_completed";
        AddEvent(id, "collaboration_synthesis_completed", "developer-collaboration");
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<CollaborationSynthesis?> GetCollaborationSynthesisAsync(
        Guid id, CancellationToken cancellationToken)
    {
        var artifact = await db.WorkflowArtifacts.AsNoTracking().SingleOrDefaultAsync(
            item => item.RunId == id &&
                item.Stage == WorkflowStages.Synthesis &&
                item.ArtifactKey == "cross-role-synthesis" &&
                item.Revision == 1 &&
                item.Status == WorkflowArtifactStatuses.Completed,
            cancellationToken);
        if (artifact is null)
        {
            return null;
        }
        var capturedSkills = (await GetDeveloperSkillSnapshotsAsync(id, cancellationToken))
            .Select(snapshot => snapshot.Skill).ToHashSet();
        var run = await GetAsync(id, cancellationToken);
        var artifactSkills = JsonSerializer.Deserialize<AgentSkillRevisionReference[]>(
            artifact.SkillRevisionsJson, JsonSerializerOptions.Web);
        var synthesis = JsonSerializer.Deserialize<CollaborationSynthesis>(
            artifact.PayloadJson, JsonSerializerOptions.Web)
            ?? throw new InvalidDataException("Persisted collaboration synthesis is invalid.");
        if (artifactSkills is null ||
            !capturedSkills.SetEquals(artifactSkills) ||
            artifactSkills.Length != capturedSkills.Count ||
            artifact.RepositoryCommit != run.RepositoryCommit)
        {
            throw new InvalidDataException("Persisted collaboration provenance does not match the run snapshot.");
        }
        InputRules.ValidateCollaborationSynthesis(synthesis, capturedSkills);
        return synthesis;
    }

    public async Task<(ArchitectureAnalysis? PreviousAnalysis, string? Feedback)>
        GetArchitectureRevisionContextAsync(Guid id, int revision, CancellationToken cancellationToken)
    {
        if (revision <= 1)
        {
            return (null, null);
        }

        var previous = await db.WorkflowArtifacts.AsNoTracking().SingleAsync(
            x => x.RunId == id && x.Stage == WorkflowStages.Architecture &&
                x.ArtifactKey == "architecture-analysis" && x.Revision == revision - 1,
            cancellationToken);
        if (previous.Decision != ArchitectureAnalysisDecisionKinds.Revise)
        {
            throw new InvalidOperationException("A new architecture revision requires a persisted revise decision.");
        }

        return (
            JsonSerializer.Deserialize<ArchitectureAnalysis>(previous.PayloadJson, JsonSerializerOptions.Web),
            previous.DecisionComment);
    }

    public async Task StoreArchitectureAnalysisAsync(
        Guid id,
        int revision,
        ArchitectureAnalysis analysis,
        string repositoryCommit,
        AgentSkillRevisionReference skillRevision,
        IReadOnlySet<string> inspectedPaths,
        CancellationToken cancellationToken)
    {
        InputRules.ValidateArchitectureAnalysis(analysis, inspectedPaths);
        var run = await GetAsync(id, cancellationToken);
        if (!run.BusinessAnalystApproved || run.FeatureSpecificationRevision < 1 ||
            run.RepositoryCommit != repositoryCommit)
        {
            throw new InvalidOperationException(
                "Architecture analysis requires the approved specification and the repository commit pinned to this run.");
        }
        var specification = await db.FeatureSpecificationRevisions.AsNoTracking().SingleOrDefaultAsync(
            x => x.RunId == id && x.Revision == run.FeatureSpecificationRevision,
            cancellationToken);
        if (specification?.Decision != BusinessAnalystDecisionKinds.Approve)
        {
            throw new InvalidOperationException("Architecture analysis requires the persisted approved specification.");
        }

        var latestRevision = await db.WorkflowArtifacts
            .Where(x => x.RunId == id && x.Stage == WorkflowStages.Architecture &&
                x.ArtifactKey == "architecture-analysis")
            .Select(x => (int?)x.Revision)
            .MaxAsync(cancellationToken) ?? 0;
        if (latestRevision >= revision)
        {
            return;
        }
        if (revision != latestRevision + 1)
        {
            throw new InvalidOperationException("Architecture analysis revisions must be stored in sequence.");
        }
        if (latestRevision > 0)
        {
            var prior = await db.WorkflowArtifacts.SingleAsync(
                x => x.RunId == id && x.Stage == WorkflowStages.Architecture &&
                    x.ArtifactKey == "architecture-analysis" && x.Revision == latestRevision,
                cancellationToken);
            if (prior.Decision != ArchitectureAnalysisDecisionKinds.Revise)
            {
                throw new InvalidOperationException("A new architecture analysis requires a persisted revise decision.");
            }
        }

        var artifact = new WorkflowArtifactRecord
        {
            RunId = id,
            Stage = WorkflowStages.Architecture,
            ArtifactKey = "architecture-analysis",
            Revision = revision,
            ArtifactType = nameof(ArchitectureAnalysis),
            PayloadJson = JsonSerializer.Serialize(analysis, JsonSerializerOptions.Web),
            InputArtifactRevisionsJson = JsonSerializer.Serialize(
                new
                {
                    businessAnalysis = run.FeatureSpecificationRevision,
                    previousArchitecture = revision > 1 ? revision - 1 : (int?)null
                },
                JsonSerializerOptions.Web),
            SkillRevisionsJson = JsonSerializer.Serialize(
                new[] { skillRevision }, JsonSerializerOptions.Web),
            RepositoryCommit = repositoryCommit,
            Status = WorkflowArtifactStatuses.AwaitingReview
        };
        db.WorkflowArtifacts.Add(artifact);
        run.Status = "awaiting_architecture_review";
        AddEvent(id, "architecture_analysis_generated", "architect");
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task RecordArchitectureAnalysisDecisionAsync(
        Guid id, ArchitectureAnalysisDecision decision, string actor, CancellationToken cancellationToken)
    {
        if (!InputRules.IsValidArchitectureAnalysisDecision(decision, out var error))
        {
            throw new ArgumentException(error, nameof(decision));
        }
        var run = await GetAsync(id, cancellationToken);
        var artifact = await db.WorkflowArtifacts.SingleOrDefaultAsync(
            x => x.RunId == id && x.Stage == WorkflowStages.Architecture &&
                x.ArtifactKey == "architecture-analysis" && x.Revision == decision.Revision,
            cancellationToken);
        if (artifact is null)
        {
            throw new InvalidOperationException("The requested architecture revision does not exist.");
        }
        if (artifact.Decision is not null)
        {
            throw new InvalidOperationException("This architecture revision already has a decision.");
        }
        if (run.Status != "awaiting_architecture_review" ||
            artifact.Status != WorkflowArtifactStatuses.AwaitingReview)
        {
            throw new InvalidOperationException("The run is not awaiting a decision on this architecture revision.");
        }
        if (decision.Decision == ArchitectureAnalysisDecisionKinds.Revise &&
            decision.Revision >= InputRules.MaximumPlanRevisions)
        {
            throw new InvalidOperationException(
                $"The revision limit of {InputRules.MaximumPlanRevisions} has been reached; approve or reject this analysis.");
        }

        var now = DateTimeOffset.UtcNow;
        artifact.Decision = decision.Decision;
        artifact.DecidedBy = actor;
        artifact.DecisionComment = string.IsNullOrWhiteSpace(decision.Comment) ? null : decision.Comment.Trim();
        artifact.DecidedAt = now;
        artifact.Status = decision.Decision switch
        {
            ArchitectureAnalysisDecisionKinds.Approve => WorkflowArtifactStatuses.Approved,
            ArchitectureAnalysisDecisionKinds.Revise => WorkflowArtifactStatuses.Superseded,
            _ => WorkflowArtifactStatuses.Rejected
        };
        run.Status = decision.Decision switch
        {
            ArchitectureAnalysisDecisionKinds.Approve => "architecture_approved",
            ArchitectureAnalysisDecisionKinds.Revise => "architecture_revision_requested",
            _ => "architecture_rejected"
        };
        db.StageArtifactReviews.Add(new StageArtifactReviewRecord
        {
            RunId = id,
            Stage = WorkflowStages.Architecture,
            ArtifactKey = artifact.ArtifactKey,
            Revision = artifact.Revision,
            ReviewerId = actor,
            Decision = decision.Decision,
            Comment = artifact.DecisionComment,
            CreatedAt = now
        });
        AddEvent(id, decision.Decision switch
        {
            ArchitectureAnalysisDecisionKinds.Approve => "architecture_approved",
            ArchitectureAnalysisDecisionKinds.Revise => "architecture_revision_requested",
            _ => "architecture_rejected"
        }, actor);
        db.Outbox.Add(new OutboxMessage
        {
            Id = Guid.NewGuid(),
            RunId = id,
            Kind = "architecture-decision",
            Revision = decision.Revision
        });
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Loads the prior specification and analyst feedback for a revision request.</summary>
    public async Task<(FeatureSpecification? PreviousSpecification, string? Feedback)>
        GetFeatureSpecificationRevisionContextAsync(Guid id, int revision, CancellationToken cancellationToken)
    {
        if (revision <= 1)
        {
            return (null, null);
        }

        var previous = await db.FeatureSpecificationRevisions.AsNoTracking()
            .SingleAsync(x => x.RunId == id && x.Revision == revision - 1, cancellationToken);
        if (previous.Decision != BusinessAnalystDecisionKinds.Revise)
        {
            throw new InvalidOperationException("A new feature-specification revision requires a persisted revise decision.");
        }

        return (
            JsonSerializer.Deserialize<FeatureSpecification>(previous.SpecificationJson, JsonSerializerOptions.Web),
            BuildBusinessAnalystFeedback(previous));
    }

    private static string? BuildBusinessAnalystFeedback(FeatureSpecificationRevisionRecord revision)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(revision.Comment))
        {
            parts.Add(revision.Comment.Trim());
        }
        if (revision.AnswersJson is not null)
        {
            var answers = JsonSerializer.Deserialize<BusinessAnalystQuestionAnswer[]>(
                revision.AnswersJson, JsonSerializerOptions.Web) ?? [];
            if (answers.Length > 0)
            {
                parts.Add("Business analyst answers to the prior specification's open questions:\n" +
                    string.Join("\n", answers.Select(answer => $"- {answer.Question}: {answer.Answer}")));
            }
        }
        return parts.Count == 0 ? null : string.Join("\n\n", parts);
    }

    /// <summary>Persists one generated specification revision and opens its BA review gate.</summary>
    public async Task StoreFeatureSpecificationAsync(
        Guid id, int revision, FeatureSpecification specification, CancellationToken cancellationToken)
    {
        InputRules.ValidateFeatureSpecification(specification);
        var run = await GetAsync(id, cancellationToken);
        if (run.BusinessAnalystApproved)
        {
            throw new InvalidOperationException("A business-approved feature specification cannot be replaced.");
        }
        if (run.FeatureSpecificationRevision >= revision)
        {
            return;
        }
        if (revision != run.FeatureSpecificationRevision + 1)
        {
            throw new InvalidOperationException("Feature-specification revisions must be stored in sequence.");
        }

        db.FeatureSpecificationRevisions.Add(new FeatureSpecificationRevisionRecord
        {
            RunId = id,
            Revision = revision,
            SpecificationJson = JsonSerializer.Serialize(specification, JsonSerializerOptions.Web)
        });
        run.FeatureSpecificationRevision = revision;
        run.Status = "awaiting_business_analyst_review";
        AddEvent(id, "business_analyst_draft_generated", "planner");
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Replaces the current unapproved draft with the BA's edits.</summary>
    public async Task UpdateFeatureSpecificationAsync(
        Guid id, int revision, FeatureSpecification specification, string actor, CancellationToken cancellationToken)
    {
        InputRules.ValidateFeatureSpecification(specification);
        var run = await GetAsync(id, cancellationToken);
        if (run.BusinessAnalystApproved || run.Status != "awaiting_business_analyst_review" ||
            revision != run.FeatureSpecificationRevision)
        {
            throw new InvalidOperationException("Only the current unapproved feature specification can be edited.");
        }
        var record = await db.FeatureSpecificationRevisions.SingleAsync(
            x => x.RunId == id && x.Revision == revision, cancellationToken);
        if (record.Decision is not null)
        {
            throw new InvalidOperationException("A decided feature specification cannot be edited.");
        }

        record.SpecificationJson = JsonSerializer.Serialize(specification, JsonSerializerOptions.Web);
        AddEvent(id, "business_analyst_specification_edited", actor);
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Records a revision-bound BA decision and dispatches it to the durable workflow.</summary>
    public async Task RecordBusinessAnalystDecisionAsync(
        Guid id, BusinessAnalystDecision decision, string actor, CancellationToken cancellationToken)
    {
        if (!InputRules.IsValidBusinessAnalystDecision(decision, out var error))
        {
            throw new ArgumentException(error, nameof(decision));
        }
        var run = await GetAsync(id, cancellationToken);
        var record = await db.FeatureSpecificationRevisions.SingleAsync(
            x => x.RunId == id && x.Revision == decision.Revision, cancellationToken);
        if (run.Status != "awaiting_business_analyst_review" ||
            run.FeatureSpecificationRevision != decision.Revision ||
            record.Decision is not null)
        {
            throw new InvalidOperationException("The run is not awaiting a decision on this feature-specification revision.");
        }
        var specification = JsonSerializer.Deserialize<FeatureSpecification>(
            record.SpecificationJson, JsonSerializerOptions.Web)
            ?? throw new InvalidDataException("The current feature specification is invalid.");
        if (decision.Decision == BusinessAnalystDecisionKinds.Revise &&
            specification.OpenQuestions.Length > 0 &&
            !DoAnswersCoverQuestions(specification.OpenQuestions, decision.Answers))
        {
            throw new InvalidOperationException("Answer every open question before requesting an AI specification update.");
        }

        if (decision.Decision == BusinessAnalystDecisionKinds.Revise &&
            decision.Revision >= InputRules.MaximumPlanRevisions)
        {
            throw new InvalidOperationException(
                $"The revision limit of {InputRules.MaximumPlanRevisions} has been reached; approve or reject this specification.");
        }

        record.Decision = decision.Decision;
        record.Comment = string.IsNullOrWhiteSpace(decision.Comment) ? null : decision.Comment.Trim();
        record.AnswersJson = decision.Answers is null
            ? null
            : JsonSerializer.Serialize(decision.Answers, JsonSerializerOptions.Web);
        record.DecidedBy = actor;
        record.DecidedAt = DateTimeOffset.UtcNow;
        run.BusinessAnalystApproved = decision.Decision == BusinessAnalystDecisionKinds.Approve;
        run.Status = decision.Decision switch
        {
            BusinessAnalystDecisionKinds.Approve => "business_analyst_approved",
            BusinessAnalystDecisionKinds.Revise => "business_analyst_revision_requested",
            _ => "feature_request_rejected"
        };
        AddEvent(id, decision.Decision switch
        {
            BusinessAnalystDecisionKinds.Approve => "business_analyst_approved",
            BusinessAnalystDecisionKinds.Revise => "business_analyst_revision_requested",
            _ => "business_analyst_rejected"
        }, actor);
        db.Outbox.Add(new OutboxMessage
        {
            Id = Guid.NewGuid(),
            RunId = id,
            Kind = "spec-decision",
            Revision = decision.Revision
        });
        await db.SaveChangesAsync(cancellationToken);
    }

    private static bool DoAnswersCoverQuestions(
        string[] questions, BusinessAnalystQuestionAnswer[]? answers)
    {
        if (answers is null || answers.Length != questions.Length)
        {
            return false;
        }
        var answered = answers.Select(answer => answer.Question.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return questions.All(question => answered.Contains(question.Trim()));
    }

    /// <summary>Loads the prior plan and reviewer feedback that a new revision must address.</summary>
    public async Task<(DraftPlan? PreviousPlan, string? Feedback)> GetRevisionContextAsync(
        Guid id, int revision, CancellationToken cancellationToken)
    {
        if (revision <= 1)
        {
            return (null, null);
        }
        var previous = await db.PlanRevisions.AsNoTracking()
            .SingleAsync(x => x.RunId == id && x.Revision == revision - 1, cancellationToken);
        if (previous.Decision != PlanDecisionKinds.Revise &&
            !(revision == 2 && await GetPlanningModeAsync(id, cancellationToken) == DeveloperPlanningModes.DraftFirst))
        {
            throw new InvalidOperationException("A new plan revision requires a persisted revise decision.");
        }

        var reviewResponses = await db.ReviewAssignments.AsNoTracking()
            .Where(x => x.RunId == id && x.Revision == revision - 1 &&
                x.RespondedAt != null && x.Response != null)
            .OrderBy(x => x.DeveloperId)
            .Select(x => new { x.DeveloperId, x.DeveloperDisplayName, x.Response })
            .ToListAsync(cancellationToken);
        var feedback = new List<string>();
        if (!string.IsNullOrWhiteSpace(previous.Comment))
        {
            feedback.Add($"Coordinator feedback:\n{previous.Comment.Trim()}");
        }
        if (reviewResponses.Count > 0)
        {
            feedback.Add(
                "Developer review feedback:\n" +
                string.Join("\n\n", reviewResponses.Select(x =>
                    $"Reviewer {x.DeveloperDisplayName} ({x.DeveloperId}):\n{x.Response}")));
        }

        return (
            JsonSerializer.Deserialize<DraftPlan>(previous.PlanJson, JsonSerializerOptions.Web),
            feedback.Count == 0 ? null : string.Join("\n\n", feedback));
    }

    public async Task<DeveloperConfigurationProfile[]> GetEnabledDeveloperProfilesAsync(
        CancellationToken cancellationToken)
    {
        var developers = await db.Developers.AsNoTracking().Where(x => x.IsEnabled)
            .OrderBy(x => x.UserId).ToListAsync(cancellationToken);
        return developers.Select(x => new DeveloperConfigurationProfile(
            x.UserId, x.DisplayName,
            JsonSerializer.Deserialize<string[]>(x.RolesJson, JsonSerializerOptions.Web) ?? [],
            x.ReviewFocus)).ToArray();
    }

    public async Task<DeveloperReviewerMatch[]?> GetSelectedReviewersAsync(
        Guid id, CancellationToken cancellationToken)
    {
        var serialized = await db.Runs.AsNoTracking().Where(x => x.Id == id)
            .Select(x => x.SelectedReviewersJson).SingleAsync(cancellationToken);
        return serialized is null
            ? null
            : JsonSerializer.Deserialize<DeveloperReviewerMatch[]>(serialized, JsonSerializerOptions.Web)
                ?? throw new InvalidDataException("Persisted reviewer selection is invalid.");
    }

    public async Task StorePlanAsync(
        Guid id, int revision, DraftPlan plan, CancellationToken cancellationToken,
        DeveloperReviewerMatch[]? reviewerMatches = null)
    {
        InputRules.ValidatePlan(plan);
        var run = await GetAsync(id, cancellationToken);
        if (!run.BusinessAnalystApproved)
        {
            throw new InvalidOperationException("Developer planning requires an approved business specification.");
        }
        if (run.FeatureSpecificationRevision > 0)
        {
            var specification = await db.FeatureSpecificationRevisions.AsNoTracking()
                .SingleOrDefaultAsync(
                    x => x.RunId == id && x.Revision == run.FeatureSpecificationRevision, cancellationToken);
            if (specification?.Decision != BusinessAnalystDecisionKinds.Approve)
            {
                throw new InvalidOperationException("Developer planning requires the persisted specification to be approved.");
            }
        }
        var approvedArchitecture = await GetCurrentArchitectureArtifactAsync(id, cancellationToken);
        if (approvedArchitecture?.Status != WorkflowArtifactStatuses.Approved ||
            approvedArchitecture.Decision != ArchitectureAnalysisDecisionKinds.Approve)
        {
            throw new InvalidOperationException("Developer planning requires human approval of the architecture analysis.");
        }
        var specialistAnalyses = await GetDeveloperSkillAnalysesAsync(id, cancellationToken);
        var capturedSkills = await GetDeveloperSkillSnapshotsAsync(id, cancellationToken);
        if (specialistAnalyses is null || specialistAnalyses.Length != capturedSkills.Length)
        {
            throw new InvalidOperationException(
                "Developer planning requires completed analysis from every captured developer skill.");
        }
        if (run.PlanRevision >= revision)
        {
            return;
        }
        if (revision != run.PlanRevision + 1)
        {
            throw new InvalidOperationException("Plan revisions must be stored in sequence.");
        }

        var json = JsonSerializer.Serialize(plan, JsonSerializerOptions.Web);
        await GetDeveloperProposalsForPlanAsync(id, revision, cancellationToken);
        run.PlanJson = json;
        run.PlanRevision = revision;
        run.Decision = null;
        run.DecisionComment = null;
        run.DecisionBy = null;
        var initialDraft = revision == 1 &&
            await GetPlanningModeAsync(id, cancellationToken) == DeveloperPlanningModes.DraftFirst;
        run.Status = initialDraft ? "initial_plan_drafted" : "awaiting_approval";
        db.PlanRevisions.Add(new PlanRevisionRecord { RunId = id, Revision = revision, PlanJson = json });
        var developers = await db.Developers.AsNoTracking()
            .OrderBy(x => x.UserId).ToListAsync(cancellationToken);
        var persistedMatches = run.SelectedReviewersJson is null
            ? null
            : JsonSerializer.Deserialize<DeveloperReviewerMatch[]>(
                run.SelectedReviewersJson, JsonSerializerOptions.Web)
                ?? throw new InvalidDataException("Persisted reviewer selection is invalid.");
        var matches = reviewerMatches ?? persistedMatches;
        if (matches is not { Length: > 0 } || matches.Length != run.ReviewerCount ||
            matches.Select(x => x.UserId).Distinct(StringComparer.Ordinal).Count() != matches.Length)
        {
            throw new InvalidOperationException(
                $"Exactly {run.ReviewerCount} distinct reviewers must be selected before the plan can be stored.");
        }
        if (persistedMatches is not null && reviewerMatches is not null &&
            (persistedMatches.Length != reviewerMatches.Length ||
             persistedMatches.Any(previous => !reviewerMatches.Any(candidate =>
                 candidate.UserId == previous.UserId &&
                 candidate.MatchedRoles.Order(StringComparer.OrdinalIgnoreCase)
                     .SequenceEqual(previous.MatchedRoles.Order(StringComparer.OrdinalIgnoreCase),
                         StringComparer.OrdinalIgnoreCase)))))
        {
            throw new InvalidOperationException("The selected reviewer group cannot change between plan revisions.");
        }
        var profiles = developers.ToDictionary(x => x.UserId, StringComparer.Ordinal);
        foreach (var match in matches)
        {
            if (!profiles.TryGetValue(match.UserId, out var developer))
            {
                throw new InvalidOperationException($"Selected reviewer '{match.UserId}' is not configured.");
            }
            var configuredRoles = persistedMatches is null
                ? JsonSerializer.Deserialize<string[]>(developer.RolesJson, JsonSerializerOptions.Web) ?? []
                : [];
            if (match.MatchedRoles is not { Length: > 0 } ||
                (persistedMatches is null &&
                 match.MatchedRoles.Any(role => !configuredRoles.Contains(role, StringComparer.OrdinalIgnoreCase))))
            {
                throw new InvalidOperationException($"Selected reviewer '{match.UserId}' has an invalid role match.");
            }
            if (persistedMatches is null && !developer.IsEnabled)
            {
                throw new InvalidOperationException($"Selected reviewer '{match.UserId}' is not enabled.");
            }
            if (!initialDraft)
            {
                db.ReviewAssignments.Add(new ReviewAssignmentRecord
                {
                    RunId = id,
                    Revision = revision,
                    DeveloperId = developer.UserId,
                    DeveloperDisplayName = developer.DisplayName,
                    ReviewFocus = developer.ReviewFocus,
                    MatchedRolesJson = JsonSerializer.Serialize(match.MatchedRoles, JsonSerializerOptions.Web),
                    ReviewPrompt = BuildReviewPrompt(run, plan, developer, match.MatchedRoles)
                });
            }
        }
        run.SelectedReviewersJson ??= JsonSerializer.Serialize(matches, JsonSerializerOptions.Web);
        AddEvent(id, "plan_generated", "planner");
        await db.SaveChangesAsync(cancellationToken);
    }

    private static string BuildReviewPrompt(
        FeatureRun run, DraftPlan plan, DeveloperRecord developer, string[] matchedRoles)
    {
        var focus = string.IsNullOrWhiteSpace(developer.ReviewFocus)
            ? "implementation feasibility, missing requirements, repository fit, and test coverage"
            : developer.ReviewFocus.Trim();
        var steps = string.Join(Environment.NewLine, plan.Steps.Select((step, index) => $"{index + 1}. {step}"));
        var questions = plan.OpenQuestions.Length == 0
            ? "(none)"
            : string.Join(Environment.NewLine, plan.OpenQuestions.Select(question => $"- {question}"));
        return $"""
            Review this feature plan as {developer.DisplayName} ({developer.UserId}).
            Your selected roles for this feature are: {string.Join(", ", matchedRoles)}.
            Focus your review on: {focus}.

            Feature request:
            {run.Text}

            Plan summary:
            {plan.Summary}

            Proposed steps:
            {steps}

            Open questions:
            {questions}

            Independently assess the plan from your selected roles. Return an explicit decision
            (approve or revise) and concise, actionable feedback. Choose revise if the plan needs
            changes; otherwise choose approve. Approval applies only to this exact revision.
            """;
    }

    public async Task RecordOutcomeAsync(Guid id, CancellationToken cancellationToken)
    {
        var run = await GetAsync(id, cancellationToken);
        if (!run.BusinessAnalystApproved)
        {
            throw new InvalidOperationException("A business analyst-approved specification is required before plan outcomes.");
        }
        var approvedArchitecture = await GetCurrentArchitectureArtifactAsync(id, cancellationToken);
        if (approvedArchitecture?.Status != WorkflowArtifactStatuses.Approved ||
            approvedArchitecture.Decision != ArchitectureAnalysisDecisionKinds.Approve)
        {
            throw new InvalidOperationException("A human-approved architecture analysis is required before plan outcomes.");
        }
        var specialistAnalyses = await GetDeveloperSkillAnalysesAsync(id, cancellationToken);
        var capturedSkills = await GetDeveloperSkillSnapshotsAsync(id, cancellationToken);
        if (specialistAnalyses is null || specialistAnalyses.Length != capturedSkills.Length)
        {
            throw new InvalidOperationException(
                "A completed analysis from every captured developer skill is required before plan outcomes.");
        }
        if (run.Decision == PlanDecisionKinds.Approve)
        {
            var approvals = await db.ReviewAssignments.AsNoTracking()
                .Where(x => x.RunId == id && x.Revision == run.PlanRevision)
                .Select(x => x.Decision).ToListAsync(cancellationToken);
            if (approvals.Count != run.ReviewerCount ||
                approvals.Any(decision => decision != DeveloperReviewDecisionKinds.Approve))
            {
                throw new InvalidOperationException(
                    "Implementation requires approval from every selected reviewer on the current plan revision.");
            }
        }
        if (run.Status is "code_change_generating" or "code_change_ready" or "plan_rejected")
        {
            return;
        }

        run.Status = run.Decision switch
        {
            PlanDecisionKinds.Approve => "code_change_generating",
            PlanDecisionKinds.Reject => "plan_rejected",
            _ => throw new InvalidOperationException("A persisted approve or reject decision is required.")
        };
        AddEvent(id, run.Decision == PlanDecisionKinds.Approve ? "plan_approved" : "plan_rejected", "workflow");
        if (run.Decision == PlanDecisionKinds.Approve)
        {
            AddEvent(id, "code_change_started", "workflow");
        }
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task RecordFailureAsync(Guid id, CancellationToken cancellationToken)
    {
        var run = await GetAsync(id, cancellationToken);
        if (run.Status == "failed")
        {
            return;
        }
        run.Status = "failed";
        var codeChangeFailed = run.Decision == PlanDecisionKinds.Approve;
        run.FailureCode = codeChangeFailed ? "code_change_failed" : "workflow_failed";
        var codeChange = await db.CodeChanges.SingleOrDefaultAsync(x => x.RunId == id, cancellationToken);
        if (codeChangeFailed && codeChange is not null)
        {
            codeChange.Status = CodeChangeStatuses.Failed;
        }
        AddEvent(id, run.FailureCode, "workflow");
        await db.SaveChangesAsync(cancellationToken);
    }

    public void AddEvent(Guid id, string kind, string actor) =>
        db.Events.Add(new RunEvent { RunId = id, Kind = kind, Actor = actor });

    private static string GetDeveloperSkillArtifactKey(AgentSkillRevisionReference reference)
    {
        var identity = $"{reference.RoleId}\n{reference.Domain}\n{reference.SkillId}";
        var digest = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(identity));
        return $"developer-skill-{Convert.ToHexString(digest).ToLowerInvariant()}";
    }
}
