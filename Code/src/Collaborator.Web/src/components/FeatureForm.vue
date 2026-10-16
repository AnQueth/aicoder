<script setup lang="ts">
import { computed, reactive, ref } from 'vue'
import { submitFeature } from '../api'
import { message, skillKey, type FeatureRequest, type Product, type Skill } from '../types'

const props = defineProps<{ products: Product[]; skills: Skill[] }>()
const emit = defineEmits<{ submitted: [runId: string] }>()
const form = reactive({ productId: '', text: '', businessAnalystId: 'default', businessAnalystSkillId: 'requirements-analysis', reviewerCount: 3 })
const selectionMode = ref<'defaults' | 'selected' | 'none'>('defaults')
const selected = ref<string[]>([])
const planningMode = ref<'proposals-first' | 'draft-first'>('proposals-first')
const busy = ref(false)
const error = ref('')
const analysts = computed(() => props.skills.filter(skill => skill.roleId === 'business-analyst' && skill.domain === 'general' && skill.isEnabled))
const specialists = computed(() => props.skills.filter(skill => skill.roleId.startsWith('developer-') && skill.isEnabled))
let pending: { fingerprint: string; key: string } | undefined

async function submit() {
  busy.value = true
  error.value = ''
  try {
    if (!form.text.trim() || !form.businessAnalystId.trim()) throw new Error('Feature text and analyst ID cannot be blank.')
    if (selectionMode.value === 'selected' && !selected.value.length) throw new Error('Select at least one specialist, or choose no specialists.')
    if (selectionMode.value === 'selected' && selected.value.some(key => !specialists.value.some(skill => skillKey(skill) === key))) {
      throw new Error('A selected specialist is no longer enabled. Review your selection before submitting.')
    }
    const body: FeatureRequest = {
      text: form.text.trim(), businessAnalystId: form.businessAnalystId.trim(),
      businessAnalystSkillId: form.businessAnalystSkillId, reviewerCount: form.reviewerCount,
      planningMode: planningMode.value,
    }
    if (selectionMode.value !== 'defaults') {
      body.developerSkills = selectionMode.value === 'none' ? [] : specialists.value
        .filter(skill => selected.value.includes(skillKey(skill)))
        .map(({ roleId, domain, skillId }) => ({ roleId, domain, skillId }))
    }
    const productId = form.productId.trim()
    const fingerprint = JSON.stringify({ productId, body })
    // A lost response must not create a second run when the same submission is retried.
    if (pending?.fingerprint !== fingerprint) pending = { fingerprint, key: crypto.randomUUID() }
    const result = await submitFeature(productId, body, pending.key)
    pending = undefined
    form.text = ''
    emit('submitted', result.runId)
  } catch (e) { error.value = message(e) }
  finally { busy.value = false }
}
</script>

<template>
  <div class="split feature-layout">
    <section class="panel">
      <p class="eyebrow">Start something useful</p><h2>Describe your next feature</h2>
      <p class="muted">Submit intent, not an approved specification. The business analyst will draft requirements for your review.</p>
      <form @submit.prevent="submit">
        <fieldset :disabled="busy">
          <label>Product ID<input v-model="form.productId" list="product-options" required pattern="[a-z0-9\-]{1,80}" maxlength="80" placeholder="Choose or enter a registered product ID"></label>
          <datalist id="product-options"><option v-for="product in products" :key="product.id" :value="product.id">{{ product.name }}</option></datalist>
          <label>Feature request<textarea v-model="form.text" required maxlength="16000" rows="9" placeholder="What should change, who needs it, and what would success look like?" /></label>
          <p class="hint">{{ form.text.length.toLocaleString() }} / 16,000 characters</p>
          <div class="form-row">
            <label>Business analyst ID<input v-model="form.businessAnalystId" required maxlength="128"></label>
            <label>Human reviewers<input v-model.number="form.reviewerCount" type="number" required min="1" max="10" step="1"></label>
          </div>
          <label>Business analyst skill<select v-model="form.businessAnalystSkillId" required>
            <option v-for="skill in analysts" :key="skill.skillId" :value="skill.skillId">{{ skill.name }} (r{{ skill.activeRevision }})</option>
          </select></label>
          <p v-if="!analysts.length" class="error">Configure an enabled business-analyst / general skill before submitting.</p>
          <label>Developer planning approach<select v-model="planningMode">
            <option value="proposals-first">Developer ideas first, then AI plan</option>
            <option value="draft-first">Initial AI draft, then developer ideas and combined AI plan</option>
          </select></label>
          <p class="hint">Every assigned developer submits an implementation proposal or explicitly chooses no additional input. The AI combines all confirmed inputs into a plan for review. Revisions ask everyone to update or reconfirm their input.</p>
          <label>Specialist selection<select v-model="selectionMode">
            <option value="defaults">All enabled specialists (registry defaults)</option>
            <option value="selected">Choose specialists for this run</option>
            <option value="none">No specialists</option>
          </select></label>
          <div v-if="selectionMode === 'selected'" class="selection-list">
            <label v-for="skill in specialists" :key="skillKey(skill)" class="check">
              <input v-model="selected" type="checkbox" :value="skillKey(skill)">
              <span>{{ skill.name }}<small>{{ skillKey(skill) }} / r{{ skill.activeRevision }}</small></span>
            </label>
            <p v-if="!specialists.length" class="muted">No enabled developer-* skills are available.</p>
          </div>
          <button type="submit" :disabled="!analysts.length">{{ busy ? 'Submitting feature...' : 'Submit feature request' }}</button>
        </fieldset>
      </form>
      <p v-if="error" role="alert" class="error">{{ error }}</p>
    </section>
    <aside class="panel guide">
      <p class="eyebrow">From idea to review branch</p><h2>People stay in the loop.</h2>
      <ol class="journey">
        <li><strong>Frame the request</strong><span>Pin the repository and draft requirements.</span></li>
        <li><strong>Review the specification</strong><span>Answer questions, approve, revise, or reject.</span></li>
        <li><strong>Review architecture</strong><span>Evaluate evidence, options, and trade-offs.</span></li>
        <li><strong>Collaborate &amp; plan</strong><span>Specialists contribute; human developers review.</span></li>
        <li><strong>Publish for review</strong><span>Generate changes on a unique review branch.</span></li>
      </ol>
      <p class="hint">AI-generated output needs human verification. Publication does not imply passing tests, acceptance, merge, or deployment.</p>
    </aside>
  </div>
</template>
