<script setup lang="ts">
import { reactive, ref } from 'vue'
import { request, segment } from '../api'
import { message, type Product } from '../types'

defineProps<{ products: Product[] }>()
const emit = defineEmits<{ saved: [] }>()
const form = reactive({ id: '', name: '', brief: '', repositoryUrl: '', repositoryBaseBranch: '' })
const editing = ref(false)
const busy = ref(false)
const error = ref('')
const notice = ref('')

function select(product?: Product) {
  Object.assign(form, product ?? { id: '', name: '', brief: '', repositoryUrl: '', repositoryBaseBranch: '' })
  form.repositoryUrl ??= ''
  form.repositoryBaseBranch ??= ''
  editing.value = !!product
  error.value = notice.value = ''
}

async function save() {
  busy.value = true
  error.value = notice.value = ''
  try {
    const repository = {
      repositoryUrl: form.repositoryUrl.trim() || null,
      repositoryBaseBranch: form.repositoryBaseBranch.trim() || null,
    }
    await request(editing.value ? `/products/${segment(form.id)}/repository` : '/products', {
      method: editing.value ? 'PUT' : 'POST',
      body: editing.value ? repository : { id: form.id, name: form.name, brief: form.brief, ...repository },
    })
    notice.value = editing.value ? 'Repository configuration saved. Existing runs keep their pinned source.' : 'Product registered.'
    emit('saved')
  } catch (e) { error.value = message(e) }
  finally { busy.value = false }
}
</script>

<template>
  <div class="split">
    <section class="panel">
      <div class="section-heading"><h2>Product directory</h2><button class="secondary" :disabled="busy" @click="select()">New product</button></div>
      <p class="muted">Each product owns a brief and a Git source. Repository access uses the workflow host's credentials.</p>
      <p v-if="!products.length" class="empty">No products yet. Register your first product to get started.</p>
      <button v-for="product in products" :key="product.id" class="list-item" :disabled="busy" @click="select(product)">
        <strong>{{ product.name }}</strong><span class="mono">{{ product.id }}</span>
        <span>{{ product.repositoryUrl || 'Repository not configured' }}</span>
      </button>
    </section>
    <section class="panel">
      <h2>{{ editing ? 'Repository settings' : 'Register a product' }}</h2>
      <p v-if="editing" class="muted">The current API allows repository changes, not product name or brief edits.</p>
      <form @submit.prevent="save">
        <fieldset :disabled="busy">
          <label>Product ID<input v-model="form.id" required pattern="[a-z0-9\-]{1,80}" maxlength="80" :disabled="editing" placeholder="customer-portal"></label>
          <label>Name<input v-model="form.name" required maxlength="200" :disabled="editing"></label>
          <label>Product brief<textarea v-model="form.brief" required maxlength="16000" rows="4" :disabled="editing" /></label>
          <label>Git repository URL<input v-model="form.repositoryUrl" maxlength="2048" placeholder="https://github.com/team/repository.git"></label>
          <p class="hint">HTTPS, ssh://, or local file:// URL. Never embed tokens or passwords.</p>
          <label>Base branch (optional)<input v-model="form.repositoryBaseBranch" maxlength="255" placeholder="Repository default branch"></label>
          <button type="submit">{{ busy ? 'Saving...' : editing ? 'Save repository' : 'Register product' }}</button>
        </fieldset>
      </form>
      <p v-if="error" role="alert" class="error">{{ error }}</p>
      <p v-if="notice" role="status" class="success">{{ notice }}</p>
    </section>
  </div>
</template>
