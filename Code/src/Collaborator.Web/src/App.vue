<script setup lang="ts">
import { computed, onBeforeUnmount, ref } from 'vue'
import { products as listProducts, request, runs as listRuns, setApiKey } from './api'
import { message, words, type Developer, type Product, type RunSummary, type Skill } from './types'
import ProductEditor from './components/ProductEditor.vue'
import DeveloperEditor from './components/DeveloperEditor.vue'
import SkillEditor from './components/SkillEditor.vue'
import FeatureForm from './components/FeatureForm.vue'
import RunDetail from './components/RunDetail.vue'

const sections = [
  { id: 'features', label: 'Submit feature', number: '01' },
  { id: 'runs', label: 'Workflow runs', number: '02' },
  { id: 'products', label: 'Products & repos', number: '03' },
  { id: 'developers', label: 'Developers & roles', number: '04' },
  { id: 'skills', label: 'Skills & prompts', number: '05' },
] as const
type Section = typeof sections[number]['id']
const section = ref<Section>('features')
const connected = ref(false)
const keyInput = ref('')
const busy = ref(false)
const error = ref('')
const products = ref<Product[]>([])
const productTotal = ref(0)
const developers = ref<Developer[]>([])
const skills = ref<Skill[]>([])
const runs = ref<RunSummary[]>([])
const runTotal = ref(0)
const selectedRun = ref('')
const runInput = ref('')
const filter = ref('')
const heading = computed(() => sections.find(item => item.id === section.value)!.label)
const visibleRuns = computed(() => runs.value.filter(run => `${run.productId} ${run.text} ${run.status} ${run.id}`.toLowerCase().includes(filter.value.toLowerCase())))
let generation = 0
onBeforeUnmount(() => { generation++; setApiKey('') })

async function refresh(connecting = false) {
  const current = ++generation
  busy.value = true
  error.value = ''
  if (connecting) setApiKey(keyInput.value.trim())
  try {
    const [nextProducts, nextDevelopers, nextSkills, nextRuns] = await Promise.all([
      listProducts(), request<Developer[]>('/developers'), request<Skill[]>('/agent-skills'), listRuns(),
    ])
    if (current !== generation) return
    products.value = nextProducts.items
    productTotal.value = nextProducts.total
    developers.value = nextDevelopers
    skills.value = nextSkills
    runs.value = nextRuns.items
    runTotal.value = nextRuns.total
    connected.value = true
    keyInput.value = ''
  } catch (e) {
    if (current !== generation) return
    error.value = message(e)
    if (connecting) setApiKey('')
  } finally { if (current === generation) busy.value = false }
}

function disconnect() {
  generation++
  setApiKey('')
  connected.value = false
  busy.value = false
  products.value = []; developers.value = []; skills.value = []; runs.value = []
  selectedRun.value = runInput.value = keyInput.value = error.value = ''
}

async function more(kind: 'products' | 'runs') {
  const current = generation
  busy.value = true
  error.value = ''
  try {
    if (kind === 'products') {
      const page = await listProducts(products.value.length)
      if (current === generation) { products.value.push(...page.items); productTotal.value = page.total }
    } else {
      const page = await listRuns(runs.value.length)
      if (current === generation) { runs.value.push(...page.items); runTotal.value = page.total }
    }
  } catch (e) { if (current === generation) error.value = message(e) }
  finally { if (current === generation) busy.value = false }
}
function openRun(id: string) {
  if (!/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(id.trim())) {
    error.value = 'Enter a valid workflow run UUID.'
    return
  }
  error.value = ''
  selectedRun.value = id.trim()
  section.value = 'runs'
}
function submitted(id: string) { openRun(id); void refresh() }
</script>

<template>
  <div class="app-shell">
    <aside class="sidebar">
      <a href="#" class="brand" @click.prevent="section = 'features'"><span class="brand-mark">co</span><span>collaborator<small>DEVELOPMENT WORKSPACE</small></span></a>
      <p class="nav-label">WORKSPACE</p>
      <nav aria-label="Main navigation">
        <button v-for="item in sections" :key="item.id" :class="{ active: section === item.id }" :aria-current="section === item.id ? 'page' : undefined" @click="section = item.id">
          <span>{{ item.number }}</span>{{ item.label }}
        </button>
      </nav>
      <div class="sidebar-note"><span class="status-dot" />Human-reviewed by design<p>Turn feature intent into a traceable review branch.</p></div>
      <p class="sidebar-footer">LOCAL DEVELOPMENT / v1</p>
    </aside>
    <main>
      <header class="topbar"><span>Workspace <span class="muted">/ {{ heading }}</span></span><div class="actions"><span class="connection">{{ connected ? 'API connected' : 'Not connected' }}</span><button v-if="connected" class="secondary" @click="disconnect">Disconnect</button></div></header>
      <div class="content">
        <div class="page-heading"><div><p class="eyebrow">Build with clarity</p><h1>{{ heading }}</h1><p class="muted">Your product context, AI expertise, and human decisions. In one place.</p></div><button v-if="connected" class="secondary" :disabled="busy" @click="refresh()">{{ busy ? 'Loading...' : 'Refresh directory' }}</button></div>
        <div class="dev-notice">Development only. Shared API keys are not user authentication. Use only repositories you are authorized to send to Copilot.</div>
        <p v-if="error" role="alert" class="error">{{ error }}</p>
        <section v-if="!connected" class="panel connect-panel">
          <p class="eyebrow">Connect your local services</p><h2>Welcome to Collaborator.</h2>
          <p class="muted">Start the existing API and workflow services, then enter the development API key from your local configuration. The key stays in memory and is cleared on disconnect or reload.</p>
          <form @submit.prevent="refresh(true)"><fieldset :disabled="busy">
            <label>Development API key<input v-model="keyInput" type="password" required minlength="32" autocomplete="off" spellcheck="false"></label>
            <button type="submit">{{ busy ? 'Connecting...' : 'Connect to API' }}</button>
          </fieldset></form>
        </section>
        <template v-else>
          <div class="stats">
            <div><span>Products</span><strong>{{ productTotal }}</strong></div>
            <div><span>Enabled reviewers</span><strong>{{ developers.filter(item => item.isEnabled).length }}</strong></div>
            <div><span>Enabled AI skills</span><strong>{{ skills.filter(item => item.isEnabled).length }}</strong></div>
            <div><span>Workflow runs</span><strong>{{ runTotal }}</strong></div>
          </div>
          <FeatureForm v-if="section === 'features'" :products="products" :skills="skills" @submitted="submitted" />
          <template v-if="section === 'products'">
            <ProductEditor :products="products" @saved="refresh()" />
            <button v-if="products.length < productTotal" class="secondary load-more" :disabled="busy" @click="more('products')">Load more products ({{ products.length }} / {{ productTotal }})</button>
          </template>
          <DeveloperEditor v-if="section === 'developers'" :developers="developers" @saved="refresh()" />
          <SkillEditor v-if="section === 'skills'" :skills="skills" @saved="refresh()" />
          <template v-if="section === 'runs'">
            <section class="panel">
              <div class="section-heading"><h2>Recent workflow runs</h2><span class="hint">{{ runs.length }} / {{ runTotal }} loaded</span></div>
              <form class="inline-form" @submit.prevent="openRun(runInput)"><label>Open run by ID<input v-model="runInput" required placeholder="Workflow run UUID"></label><button>Open run</button></form>
              <label>Filter loaded runs<input v-model="filter" type="search" placeholder="Search by product, feature, state, or ID"></label>
              <p v-if="!visibleRuns.length" class="empty">No matching runs. Submit a feature to start a workflow.</p>
              <div class="run-grid"><button v-for="run in visibleRuns" :key="run.id" class="run-card" :class="{ selected: run.id === selectedRun }" @click="openRun(run.id)">
                <span class="badge">{{ words(run.status) }}</span><strong>{{ run.text }}</strong><span>{{ run.productId }} / {{ new Date(run.createdAt).toLocaleString() }}</span><small class="mono">{{ run.id }}</small>
              </button></div>
              <button v-if="runs.length < runTotal" class="secondary load-more" :disabled="busy" @click="more('runs')">Load more runs</button>
            </section>
            <RunDetail v-if="selectedRun" :run-id="selectedRun" />
          </template>
        </template>
        <footer class="page-footer">Collaborator / Ideas become decisions. Decisions become reviewable changes.</footer>
      </div>
    </main>
  </div>
</template>
