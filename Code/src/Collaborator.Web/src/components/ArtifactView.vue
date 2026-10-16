<script setup lang="ts">
import type { JsonValue } from '../types'
import { words } from '../types'
defineProps<{ value: JsonValue }>()
</script>

<template>
  <p v-if="value === null" class="muted">Not available yet</p>
  <ul v-else-if="Array.isArray(value)" class="artifact-list">
    <li v-for="(item, index) in value" :key="index"><ArtifactView :value="item" /></li>
    <li v-if="!value.length" class="muted">None</li>
  </ul>
  <dl v-else-if="typeof value === 'object'" class="artifact">
    <div v-for="(item, key) in value" :key="key">
      <dt>{{ words(String(key)) }}</dt>
      <dd><ArtifactView :value="item" /></dd>
    </div>
  </dl>
  <p v-else class="preserve">{{ value }}</p>
</template>
