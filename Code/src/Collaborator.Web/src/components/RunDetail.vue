<script setup lang="ts">
import { computed, onBeforeUnmount, ref, watch } from 'vue'
import { request, segment } from '../api'
import { message, words, type Assignment, type DeveloperProposalRound, type JsonValue, type Run, type RunEvent, type Specification } from '../types'
import ArtifactView from './ArtifactView.vue'
import DecisionForm from './DecisionForm.vue'
import DeveloperProposalForm from './DeveloperProposalForm.vue'

const props = defineProps<{ runId: string }>()
const run = ref<Run>()
const assignments = ref<Assignment[]>([])
const events = ref<RunEvent[]>([])
const artifacts = ref<Record<string, JsonValue>>({})
const proposalRounds = ref<DeveloperProposalRound[]>([])
const currentProposalRound = computed(() => proposalRounds.value.at(-1))
const error = ref('')
const busy = ref(false)
const updatedAt = ref('')
const autoRefresh = ref(true)
const editing = ref(false)
const editSpec = ref('')
const editingRevision = ref(0)
const editError = ref('')
const savingSpec = ref(false)
let controller: AbortController | undefined
let timer: ReturnType<typeof setTimeout> | undefined
const artifactRoutes = ['feature-specifications', 'architecture', 'specialist-analyses', 'collaboration', 'plans'] as const
const currentReviews = computed(() => assignments.value.filter(item => item.revision === run.value?.planRevision))
const allResponded = computed(() => currentReviews.value.length === run.value?.reviewerCount && currentReviews.value.every(item => item.decision !== null))
const allApproved = computed(() => allResponded.value && currentReviews.value.every(item => item.decision === 'approve'))
const terminal = computed(() => ['code_change_ready', 'failed', 'blocked', 'feature_request_rejected', 'architecture_rejected', 'plan_rejected'].includes(run.value?.status ?? ''))
const currentArchitecture = computed(() => {
  const history = artifacts.value.architecture
  if (!Array.isArray(history)) {
    return null
  }
  const artifact = history.find(item => item !== null && typeof item === 'object' && !Array.isArray(item)
    && item.revision === run.value?.architectureRevision)
  return artifact && typeof artifact === 'object' && !Array.isArray(artifact) ? artifact.analysis ?? null : null
})
const reviewTask = computed(() => {
  if (run.value?.status === 'awaiting_developer_proposals') {
    return 'Each assigned developer: describe how you want to implement the approved feature, or explicitly choose no additional input. The AI waits for everyone, then creates one combined plan for review.'
  }
  if (run.value?.status === 'developer_proposals_ready') {
    return 'All developer inputs are confirmed. The AI will combine them into the next plan. This is not implementation approval.'
  }
  if (run.value?.status === 'awaiting_business_analyst_review') {
    return 'Review the feature specification below. Decide what the feature must do before technical planning begins.'
  }
  if (run.value?.status === 'awaiting_architecture_review') {
    return 'Review the current architecture analysis below, not its history. Decide whether the technical approach fits the approved requirements.'
  }
  if (run.value?.status === 'awaiting_approval') {
    return 'Review the current implementation plan below. First record each assigned reviewer response, then make the separate final decision that authorizes implementation.'
  }
  return ''
})

async function load() {
  clearTimeout(timer)
  controller?.abort()
  controller = new AbortController()
  const signal = controller.signal
  const id = props.runId
  busy.value = true
  error.value = ''
  try {
    const base = `/runs/${segment(id)}`
    const [nextRun, nextAssignments, nextProposals, ...nextArtifacts] = await Promise.all([
      request<Run>(base, { signal }),
      request<Assignment[]>(`${base}/assignments`, { signal }),
      request<DeveloperProposalRound[]>(`${base}/developer-proposals`, { signal }),
      ...artifactRoutes.map(route => request<JsonValue>(`${base}/${route}`, { signal })),
    ])
    // Read every event page, not just the first 100 events.
    const nextEvents: RunEvent[] = []
    let after = 0
    while (true) {
      const page = await request<RunEvent[]>(`${base}/events?after=${after}`, { signal })
      nextEvents.push(...page)
      if (page.length < 100) break
      after = page[page.length - 1]!.id
    }
    if (signal.aborted) return
    run.value = nextRun
    assignments.value = nextAssignments
    proposalRounds.value = nextProposals
    artifacts.value = Object.fromEntries(artifactRoutes.map((route, index) => [route, nextArtifacts[index]!]))
    events.value = nextEvents
    updatedAt.value = new Date().toLocaleTimeString()
  } catch (e) { if (!signal.aborted) error.value = message(e) }
  finally {
    if (!signal.aborted) {
      busy.value = false
      if (autoRefresh.value && !terminal.value) timer = setTimeout(load, 5000)
    }
  }
}

watch(() => props.runId, () => {
  run.value = undefined
  assignments.value = []
  events.value = []
  artifacts.value = {}
  proposalRounds.value = []
  editing.value = false
  editError.value = ''
  void load()
}, { immediate: true })
watch(autoRefresh, enabled => { if (enabled) void load(); else clearTimeout(timer) })
onBeforeUnmount(() => { controller?.abort(); clearTimeout(timer) })

function startEdit() {
  editSpec.value = JSON.stringify(run.value?.featureSpecification, null, 2)
  editingRevision.value = run.value!.featureSpecificationRevision
  editError.value = ''
  editing.value = true
}
async function saveSpecification() {
  if (!run.value) return
  savingSpec.value = true
  editError.value = ''
  try {
    // The server validates the complete specification schema and revision state.
    const specification: Specification = JSON.parse(editSpec.value)
    await request(`/runs/${segment(props.runId)}/feature-specifications/${editingRevision.value}`, {
      method: 'PUT', body: specification,
    })
    editing.value = false
    await load()
  } catch (e) { editError.value = message(e) }
  finally { savingSpec.value = false }
}
</script>

<template>
  <section class="panel run-detail">
    <div class="section-heading">
      <h2>Workflow workspace</h2><button class="secondary" :disabled="busy" @click="load">{{ busy ? 'Refreshing...' : 'Refresh now' }}</button>
    </div>
    <p class="mono">{{ runId }}</p>
    <label class="check"><input v-model="autoRefresh" type="checkbox">Refresh every 5 seconds while active</label>
    <p class="hint" role="status">{{ busy ? 'Loading workflow artifacts...' : `Last updated: ${updatedAt || 'not yet loaded'}` }}</p>
    <p v-if="error" role="alert" class="error">{{ error }} Displayed data may be stale. Review actions are disabled until refresh succeeds.</p>
    <template v-if="run">
      <div class="run-banner"><span class="badge">{{ words(run.status) }}</span><strong>{{ run.productId }}</strong></div>
      <aside v-if="reviewTask" class="review-task" aria-label="Current review task">
        <h3>Your next step</h3><p>{{ reviewTask }}</p>
        <p class="hint">Earlier documents and history are reference material, not additional approval requests.</p>
      </aside>
      <p class="preserve">{{ run.text }}</p>
      <p v-if="run.failureCode" class="error">Workflow failure: {{ run.failureCode }}</p>
      <details><summary>Pinned repository</summary>
        <dl class="artifact"><div><dt>Source</dt><dd>{{ run.repositoryUrl }}</dd></div><div><dt>Base branch</dt><dd>{{ run.repositoryBaseBranch || 'Repository default' }}</dd></div><div><dt>Commit</dt><dd class="mono">{{ run.repositoryCommit || 'Not pinned yet' }}</dd></div></dl>
      </details>
      <section v-if="run.featureSpecification" id="current-specification" class="review-section" tabindex="-1">
        <h3>Business analyst specification / r{{ run.featureSpecificationRevision }}</h3>
        <p class="hint">{{ run.status === 'awaiting_business_analyst_review' ? 'Review this document: your specification decision is directly below it.' : 'Reference document: this is not the current approval request.' }}</p>
        <ArtifactView v-if="run.status === 'awaiting_business_analyst_review'" :value="{ ...run.featureSpecification }" />
        <details v-else><summary>Read specification (reference only)</summary><ArtifactView :value="{ ...run.featureSpecification }" /></details>
        <template v-if="run.status === 'awaiting_business_analyst_review'">
          <button v-if="!editing" class="secondary" :disabled="!!error || busy" @click="startEdit">Edit specification JSON</button>
          <form v-else @submit.prevent="saveSpecification">
            <label>Complete specification JSON<textarea v-model="editSpec" class="mono" rows="18" required :disabled="savingSpec" /></label>
            <div class="actions"><button :disabled="savingSpec || !!error || busy">Save specification</button><button type="button" class="secondary" :disabled="savingSpec" @click="editing = false">Cancel</button></div>
            <p v-if="editError" role="alert" class="error">{{ editError }}</p>
          </form>
          <DecisionForm v-if="!editing" :key="`ba-${run.id}-${run.featureSpecificationRevision}`" :run-id="run.id" :revision="run.featureSpecificationRevision" stage="business-analyst" document-id="current-specification" :actor-id="run.businessAnalystId" :questions="run.featureSpecification.openQuestions" :disabled="!!error || busy" @saved="load" />
        </template>
      </section>
      <section v-if="run.architectureRevision > 0" id="current-architecture" class="review-section" tabindex="-1">
        <h3>Current architecture analysis / r{{ run.architectureRevision }}</h3>
        <template v-if="currentArchitecture">
          <ArtifactView v-if="run.status === 'awaiting_architecture_review'" :value="currentArchitecture" />
          <details v-else><summary>Read architecture (reference only)</summary><ArtifactView :value="currentArchitecture" /></details>
        </template>
        <p v-else role="alert" class="error">The architecture document for revision {{ run.architectureRevision }} is unavailable. Refresh before reviewing.</p>
        <DecisionForm v-if="run.status === 'awaiting_architecture_review'" :key="`architecture-${run.id}-${run.architectureRevision}`" :run-id="run.id" :revision="run.architectureRevision" stage="architecture" document-id="current-architecture" :disabled="!currentArchitecture || !!error || busy" @saved="load" />
      </section>
      <section v-if="run.plan" id="current-plan" class="review-section" tabindex="-1">
        <h3>{{ run.planningMode === 'draft-first' && run.planRevision === 1 ? 'Initial AI draft (input for developer proposals)' : 'Current implementation plan' }} / r{{ run.planRevision }}</h3><ArtifactView :value="{ ...run.plan }" />
      </section>
      <section v-if="currentProposalRound" class="review-section">
        <h3>Developer implementation ideas / target plan r{{ currentProposalRound.revision }}</h3>
        <p>{{ currentProposalRound.developers.filter(item => item.submittedAt).length }} / {{ currentProposalRound.developers.length }} inputs confirmed.</p>
        <p class="hint">The AI combines everyone's input, preserving unresolved conflicts for review. On a plan revision, everyone must update or reconfirm their proposal.</p>
        <template v-if="run.status === 'awaiting_developer_proposals' && currentProposalRound.status === 'collecting'">
          <DeveloperProposalForm v-for="developer in currentProposalRound.developers"
            :key="`${run.id}-${currentProposalRound.revision}-${developer.userId}`"
            :run-id="run.id" :revision="currentProposalRound.revision" :developer="developer" :disabled="!!error || busy" @saved="load" />
        </template>
        <p v-else class="hint">This round is closed. Confirmed inputs are retained below as plan evidence.</p>
        <details><summary>Confirmed inputs and previous rounds (reference only)</summary>
          <ArtifactView :value="proposalRounds.map(round => ({ revision: round.revision, status: round.status, developers: round.developers.map(developer => ({ ...developer })) }))" />
        </details>
      </section>
      <section v-if="assignments.length && run.status !== 'awaiting_developer_proposals' && run.status !== 'initial_plan_drafted' && run.status !== 'developer_proposals_ready'" class="review-section">
        <h3>Developer review assignments</h3>
        <article v-for="assignment in assignments" :key="`${run.id}-${assignment.revision}-${assignment.userId}`" class="assignment">
          <h4>{{ assignment.developerDisplayName }} / r{{ assignment.revision }} <span class="badge">{{ assignment.decision || assignment.status }}</span></h4>
          <p class="hint">{{ assignment.userId }} / {{ assignment.matchedRoles.join(', ') }}</p>
          <p>{{ assignment.reviewFocus }}</p><pre>{{ assignment.reviewPrompt }}</pre><p>{{ assignment.response }}</p>
          <DecisionForm v-if="run.status === 'awaiting_approval' && assignment.revision === run.planRevision && !assignment.decision" :run-id="run.id" :revision="assignment.revision" stage="assignment" document-id="current-plan" :reviewer-name="assignment.developerDisplayName" :actor-id="assignment.userId" :disabled="!run.plan || !!error || busy" @saved="load" />
        </article>
      </section>
      <template v-if="run.status === 'awaiting_approval'">
        <p v-if="!allResponded" class="hint">All {{ run.reviewerCount }} assigned developers must respond before a final plan decision.</p>
        <DecisionForm :key="`plan-${run.id}-${run.planRevision}`" :run-id="run.id" :revision="run.planRevision" stage="plan" document-id="current-plan" :can-approve="allApproved" :disabled="!run.plan || !allResponded || !!error || busy" @saved="load" />
      </template>
      <section v-if="run.codeChange" class="review-section">
        <h3>Implementation handoff</h3><ArtifactView :value="{ ...run.codeChange }" />
        <p class="hint">Ready for review is not a release approval. Verify the branch independently before merging.</p>
      </section>
      <details v-for="route in artifactRoutes" :key="route">
        <summary>{{ words(route) }} / history &amp; evidence (reference only)</summary>
        <ArtifactView v-if="artifacts[route] !== undefined" :value="artifacts[route]!" />
      </details>
      <details open><summary>Activity timeline ({{ events.length }})</summary>
        <ol class="timeline"><li v-for="event in events" :key="event.id"><strong>{{ words(event.kind) }}</strong><span>{{ event.actor }} / {{ new Date(event.createdAt).toLocaleString() }}</span></li></ol>
        <p v-if="!events.length" class="muted">No events yet.</p>
      </details>
    </template>
  </section>
</template>
