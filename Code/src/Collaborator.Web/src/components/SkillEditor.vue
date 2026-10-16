<script setup lang="ts">
import { onBeforeUnmount, reactive, ref } from 'vue'
import { request, skillPath } from '../api'
import { message, skillKey, type Skill, type SkillRevision } from '../types'

defineProps<{ skills: Skill[] }>()
const emit = defineEmits<{ saved: [] }>()
const form = reactive({ roleId: 'developer-backend', domain: 'general', skillId: '', name: '', prompt: '', isEnabled: true, changeReason: '', actorId: '' })
const editing = ref(false)
const busy = ref(false)
const loadingHistory = ref(false)
const history = ref<SkillRevision[]>([])
const error = ref('')
const notice = ref('')
let controller: AbortController | undefined
onBeforeUnmount(() => controller?.abort())
async function select(skill?: Skill) {
  controller?.abort()
  controller = new AbortController()
  const signal = controller.signal
  Object.assign(form, skill ?? { roleId: 'developer-backend', domain: 'general', skillId: '', name: '', prompt: '', isEnabled: true })
  form.changeReason = ''
  editing.value = !!skill
  history.value = []
  error.value = notice.value = ''
  loadingHistory.value = !!skill
  if (!skill) return
  try { history.value = await request<SkillRevision[]>(skillPath(skill), { signal }) }
  catch (e) { if (!signal.aborted) error.value = message(e) }
  finally { if (!signal.aborted) loadingHistory.value = false }
}
async function save() {
  busy.value = true
  error.value = notice.value = ''
  try {
    await request(skillPath(form), { method: 'POST', body: {
      name: form.name, prompt: form.prompt, isEnabled: form.isEnabled,
      changeReason: form.changeReason || null, actorId: form.actorId.trim() || null,
    } })
    notice.value = 'New immutable skill revision saved. Existing runs keep their captured prompts.'
    emit('saved')
    editing.value = true
    history.value = await request<SkillRevision[]>(skillPath(form))
  } catch (e) { error.value = message(e) }
  finally { busy.value = false }
}
</script>

<template>
  <div class="split">
    <section class="panel">
      <div class="section-heading"><h2>Role &amp; prompt registry</h2><button class="secondary" :disabled="busy" @click="select()">New skill</button></div>
      <p class="muted">AI roles live on skills. Use developer-* roles for selectable specialists, business-analyst / general for intake, or architect / general / architecture-analysis for architecture.</p>
      <p v-if="!skills.length" class="empty">No skills configured.</p>
      <button v-for="skill in skills" :key="skillKey(skill)" class="list-item" :disabled="busy" @click="select(skill)">
        <strong>{{ skill.name }} <span class="badge">r{{ skill.activeRevision }}</span></strong>
        <span class="mono">{{ skillKey(skill) }}</span><span>{{ skill.isEnabled ? 'Enabled' : 'Disabled' }}</span>
      </button>
    </section>
    <section class="panel">
      <h2>{{ editing ? 'Create next prompt revision' : 'Create a role skill' }}</h2>
      <form @submit.prevent="save">
        <fieldset :disabled="busy || loadingHistory">
          <div class="form-row">
            <label>Role ID<input v-model="form.roleId" required pattern="[a-z0-9\-]{1,80}" maxlength="80" :disabled="editing"></label>
            <label>Domain<input v-model="form.domain" required pattern="[a-z0-9\-]{1,80}" maxlength="80" :disabled="editing"></label>
          </div>
          <label>Skill ID<input v-model="form.skillId" required pattern="[a-z0-9\-]{1,80}" maxlength="80" :disabled="editing"></label>
          <label>Name<input v-model="form.name" required maxlength="200"></label>
          <label>Prompt<textarea v-model="form.prompt" required maxlength="12000" rows="10" placeholder="Define expertise, required evidence, deliverables, and verification needs." /></label>
          <p class="hint">{{ form.prompt.length.toLocaleString() }} / 12,000 characters. Do not include secrets or personal data.</p>
          <label>Change reason<textarea v-model="form.changeReason" maxlength="4000" rows="2" /></label>
          <label>Author ID (optional)<input v-model="form.actorId" maxlength="128"></label>
          <label class="check"><input v-model="form.isEnabled" type="checkbox">Enabled for future runs</label>
          <button type="submit">{{ busy ? 'Saving revision...' : 'Save new revision' }}</button>
        </fieldset>
      </form>
      <p v-if="loadingHistory" role="status">Loading revision history...</p>
      <p v-if="error" role="alert" class="error">{{ error }}</p>
      <p v-if="notice" role="status" class="success">{{ notice }}</p>
      <details v-if="history.length"><summary>Revision history ({{ history.length }})</summary>
        <article v-for="revision in history" :key="revision.revision" class="history-item">
          <h3>Revision {{ revision.revision }} / {{ revision.name }}</h3>
          <p class="muted">{{ revision.createdAt }} / {{ revision.createdBy || 'Unspecified author' }}</p>
          <p>{{ revision.changeReason }}</p><pre>{{ revision.prompt }}</pre>
        </article>
      </details>
    </section>
  </div>
</template>
