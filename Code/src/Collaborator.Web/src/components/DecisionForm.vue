<script setup lang="ts">
import { computed, ref } from 'vue'
import { request, segment } from '../api'
import { message } from '../types'

const props = withDefaults(defineProps<{
  runId: string
  revision: number
  stage: 'business-analyst' | 'architecture' | 'plan' | 'assignment'
  actorId?: string
  reviewerName?: string
  documentId?: string
  questions?: string[]
  canApprove?: boolean
  disabled?: boolean
}>(), { canApprove: true })
const emit = defineEmits<{ saved: [] }>()
const decision = ref('approve')
const comment = ref('')
const answers = ref<Record<string, string>>({})
const busy = ref(false)
const error = ref('')
const notice = ref('')
const review = computed(() => ({
  'business-analyst': {
    title: 'Feature specification', scope: 'the requirements, acceptance criteria, constraints, and open questions',
    approve: 'Approve specification', revise: 'Request specification revision', reject: 'Reject feature',
    next: 'Approval moves this specification to architecture analysis. It does not approve a plan or code.',
    revision: 'Your answers and feedback ask the analyst to generate a new specification for another review.',
  },
  architecture: {
    title: 'Architecture analysis', scope: 'the proposed technical approach, risks, and boundaries within the approved specification',
    approve: 'Approve architecture', revise: 'Request architecture revision', reject: 'Reject architecture',
    next: 'Approval starts specialist analysis and implementation planning. It does not approve generated code.',
    revision: 'Your feedback asks for a new architecture analysis. New feature requirements cannot amend the approved specification at this stage.',
  },
  assignment: {
    title: 'Implementation plan', scope: 'the current plan from your assigned reviewer role, not the specialist prompt or a code change',
    approve: 'Approve plan as reviewer', revise: 'Request plan changes as reviewer', reject: '',
    next: 'This records only your reviewer response. A separate final plan decision is still required before implementation.',
    revision: 'Your feedback is recorded for the final plan decision. It does not start a new revision by itself.',
  },
  plan: {
    title: 'Implementation plan', scope: 'the complete implementation plan and the assigned reviewers\' responses',
    approve: 'Approve plan and start implementation', revise: 'Request new plan revision', reject: 'Reject plan',
    next: 'Approval authorizes code generation and publication of a review branch. It does not merge or deploy code.',
    revision: 'Your feedback requests a new plan. Runs with developer implementation input ask everyone to update or reconfirm their proposals first. The revised plan needs fresh reviewer responses and a final decision.',
  },
}[props.stage]))
const target = computed(() => `${review.value.title} / revision ${props.revision}`)
const actionLabel = computed(() => decision.value === 'approve' ? review.value.approve
  : decision.value === 'revise' ? review.value.revise : review.value.reject)
const questionsAnswered = computed(() => (props.questions ?? []).every(question => (answers.value[question] ?? '').trim()))
const requiresAnswers = computed(() => props.stage === 'business-analyst' && decision.value === 'revise')
const requiresComment = computed(() => decision.value !== 'approve' && !(
  requiresAnswers.value && (props.questions?.length ?? 0) > 0 && questionsAnswered.value
))
async function save() {
  busy.value = true
  error.value = notice.value = ''
  try {
    const questionAnswers = (props.questions ?? []).map(question => ({ question, answer: (answers.value[question] ?? '').trim() }))
    if (requiresAnswers.value && !questionsAnswered.value) {
      throw new Error('Answer every open question before requesting an AI specification update.')
    }
    if (requiresComment.value && !comment.value.trim()) throw new Error('Provide feedback for a revision or rejection.')
    const endpoint = props.stage === 'assignment'
      ? `assignments/${segment(props.actorId ?? '')}/response` : `${props.stage}-decisions`
    await request(`/runs/${segment(props.runId)}/${endpoint}`, {
      method: 'POST',
      body: props.stage === 'assignment' ? { decision: decision.value, response: comment.value.trim() || null } : {
        revision: props.revision, decision: decision.value, comment: comment.value.trim() || null,
        ...(props.actorId ? { actorId: props.actorId } : {}),
        ...(props.stage === 'business-analyst' ? { answers: questionAnswers.filter(item => item.answer) } : {}),
      },
    })
    notice.value = `${actionLabel.value} recorded for ${target.value}. Refreshing workflow state...`
    emit('saved')
  } catch (e) { error.value = message(e) }
  finally { busy.value = false }
}
</script>

<template>
  <form class="decision-form" @submit.prevent="save">
    <fieldset :disabled="busy || disabled">
      <h3>{{ stage === 'assignment' ? 'Reviewer response' : stage === 'plan' ? 'Final plan decision' : 'Your review' }}: {{ target }}</h3>
      <p v-if="stage === 'assignment'" class="hint">Responding as {{ reviewerName || actorId }}.</p>
      <p>You are reviewing {{ review.scope }}.</p>
      <a v-if="documentId" class="review-document-link" :href="`#${documentId}`">Read {{ review.title.toLowerCase() }} revision {{ revision }} above</a>
      <p class="review-consequence">{{ review.next }}</p>
      <p v-if="stage === 'plan' && canApprove === false" class="hint">Approval requires every selected developer to approve this revision.</p>
      <label>Decision<select v-model="decision">
        <option value="approve" :disabled="canApprove === false">{{ review.approve }}</option>
        <option value="revise" :disabled="stage !== 'assignment' && revision >= 5">{{ review.revise }}</option>
        <option v-if="stage !== 'assignment'" value="reject">{{ review.reject }}</option>
      </select></label>
      <p v-if="decision === 'revise'" class="hint">{{ review.revision }}</p>
      <p v-else-if="decision === 'reject'" class="hint">Rejection stops this feature run at this stage.</p>
      <p v-if="questions?.length" class="hint">Answer all questions to request an AI revision. Approval accepts the specification as it stands.</p>
      <label v-for="question in questions" :key="question">{{ question }}
        <textarea v-model="answers[question]" :required="requiresAnswers" maxlength="4000" rows="2" />
      </label>
      <label>{{ requiresComment ? 'Feedback' : 'Comment' }} on {{ review.title.toLowerCase() }} revision {{ revision }} ({{ requiresComment ? 'required' : 'optional' }})
        <textarea v-model="comment" :required="requiresComment" maxlength="4000" rows="3" />
      </label>
      <p class="hint">This action applies only to {{ target }}.</p>
      <button type="submit" :disabled="decision === 'approve' && canApprove === false">{{ busy ? 'Recording...' : `${actionLabel} (r${revision})` }}</button>
    </fieldset>
    <p v-if="error" role="alert" class="error">{{ error }}</p>
    <p v-if="notice" role="status" class="success">{{ notice }}</p>
  </form>
</template>
