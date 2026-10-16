<script setup lang="ts">
import { reactive, ref } from 'vue'
import { request, segment } from '../api'
import { message, type Developer } from '../types'

defineProps<{ developers: Developer[] }>()
const emit = defineEmits<{ saved: [] }>()
const form = reactive({ userId: '', displayName: '', roles: '', reviewFocus: '', isEnabled: true })
const editing = ref(false)
const busy = ref(false)
const error = ref('')
const notice = ref('')
function select(developer?: Developer) {
  Object.assign(form, developer ? {
    ...developer, roles: (JSON.parse(developer.rolesJson) as string[]).join('\n'), reviewFocus: developer.reviewFocus ?? '',
  } : { userId: '', displayName: '', roles: '', reviewFocus: '', isEnabled: true })
  editing.value = !!developer
  error.value = notice.value = ''
}
async function save() {
  busy.value = true
  error.value = notice.value = ''
  try {
    const roles = form.roles.split('\n').map(value => value.trim()).filter(Boolean)
    if (roles.length < 1 || roles.length > 10 || new Set(roles.map(role => role.toLowerCase())).size !== roles.length || roles.some(role => role.length > 80)) {
      throw new Error('Enter 1-10 unique roles, each at most 80 characters.')
    }
    await request(`/developers/${segment(form.userId.trim())}`, {
      method: 'PUT', body: { displayName: form.displayName, reviewFocus: form.reviewFocus || null, isEnabled: form.isEnabled, roles },
    })
    notice.value = 'Developer configuration saved.'
    emit('saved')
  } catch (e) { error.value = message(e) }
  finally { busy.value = false }
}
</script>

<template>
  <div class="split">
    <section class="panel">
      <div class="section-heading"><h2>Human reviewers</h2><button class="secondary" :disabled="busy" @click="select()">New developer</button></div>
      <p class="muted">Roles describe a developer's expertise for reviewer selection. These are not authorization permissions.</p>
      <p v-if="!developers.length" class="empty">No developers configured.</p>
      <button v-for="developer in developers" :key="developer.userId" class="list-item" :disabled="busy" @click="select(developer)">
        <strong>{{ developer.displayName }}</strong><span>{{ developer.userId }} / {{ developer.isEnabled ? 'Enabled' : 'Disabled' }}</span>
        <span>{{ (JSON.parse(developer.rolesJson) as string[]).join(' / ') }}</span>
      </button>
    </section>
    <section class="panel">
      <h2>{{ editing ? 'Edit developer' : 'Configure a developer' }}</h2>
      <form @submit.prevent="save">
        <fieldset :disabled="busy">
          <label>Developer ID<input v-model="form.userId" required maxlength="128" :disabled="editing"></label>
          <label>Display name<input v-model="form.displayName" required maxlength="200"></label>
          <label>Roles (one per line)<textarea v-model="form.roles" required rows="4" placeholder="backend&#10;database&#10;quality-assurance" /></label>
          <label>Review focus<textarea v-model="form.reviewFocus" maxlength="1000" rows="3" /></label>
          <label class="check"><input v-model="form.isEnabled" type="checkbox">Enabled for selection</label>
          <button type="submit">{{ busy ? 'Saving...' : 'Save developer' }}</button>
        </fieldset>
      </form>
      <p v-if="error" role="alert" class="error">{{ error }}</p>
      <p v-if="notice" role="status" class="success">{{ notice }}</p>
    </section>
  </div>
</template>
