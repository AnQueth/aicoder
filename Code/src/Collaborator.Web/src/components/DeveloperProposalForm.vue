<script setup lang="ts">
import { ref } from 'vue'
import { request, segment } from '../api'
import { message, type DeveloperProposal } from '../types'

const props = defineProps<{ runId: string; revision: number; developer: DeveloperProposal; disabled: boolean }>()
const emit = defineEmits<{ saved: [] }>()
const proposal = ref(props.developer.proposal ?? '')
const noAdditionalInput = ref(props.developer.noAdditionalInput)
const busy = ref(false)
const error = ref('')
const notice = ref('')
async function save() {
  busy.value = true
  error.value = notice.value = ''
  try {
    if (!noAdditionalInput.value && !proposal.value.trim()) {
      throw new Error('Describe your implementation approach or explicitly choose no additional input.')
    }
    await request(`/runs/${segment(props.runId)}/developer-proposals/${segment(props.developer.userId)}`, {
      method: 'POST', body: { revision: props.revision, proposal: noAdditionalInput.value ? null : proposal.value.trim(), noAdditionalInput: noAdditionalInput.value },
    })
    notice.value = `Input confirmed for plan revision ${props.revision}. The AI waits for every assigned developer.`
    emit('saved')
  } catch (e) { error.value = message(e) }
  finally { busy.value = false }
}
</script>

<template>
  <form class="decision-form proposal-form" @submit.prevent="save">
    <fieldset :disabled="disabled || busy">
      <h4>{{ developer.displayName }}: implementation input for plan revision {{ revision }}</h4>
      <p class="hint">{{ developer.userId }} / {{ developer.matchedRoles.join(', ') }}</p>
      <p v-if="developer.reviewFocus">{{ developer.reviewFocus }}</p>
      <p>How would you implement the approved feature? Include your approach, affected code, trade-offs, and tests. This is input to the AI plan, not approval to implement.</p>
      <p v-if="developer.submittedAt" class="hint">Input confirmed. You may update it until everyone has responded and the round closes.</p>
      <p v-else-if="developer.proposal || developer.noAdditionalInput" class="hint">Previous input is prefilled for this new round. Update or reconfirm it; it has not been submitted for this revision.</p>
      <label class="check"><input v-model="noAdditionalInput" type="checkbox">No additional input for this revision</label>
      <label>Implementation proposal for plan revision {{ revision }}
        <textarea v-model="proposal" :disabled="noAdditionalInput" :required="!noAdditionalInput" rows="5" maxlength="4000" />
      </label>
      <button>{{ busy ? 'Confirming...' : `Confirm implementation input (r${revision})` }}</button>
    </fieldset>
    <p v-if="error" role="alert" class="error">{{ error }}</p>
    <p v-if="notice" role="status" class="success">{{ notice }}</p>
  </form>
</template>
