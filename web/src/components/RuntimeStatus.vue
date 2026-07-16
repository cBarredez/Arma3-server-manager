<script setup>
import { computed, onMounted, onUnmounted, ref } from 'vue';
import { lifecyclePresentation, normalizeServerStatus } from '../scripts/server-lifecycle.js';

const status = ref(normalizeServerStatus());
const presentation = computed(() => lifecyclePresentation(status.value));

function applyStatus(event) {
  status.value = normalizeServerStatus(event.detail);
}

onMounted(async () => {
  window.addEventListener('arma3:status', applyStatus);
  const base = (window.ARMA3_API_BASE || '').replace(/\/$/, '');
  try {
    const response = await fetch(`${base}/api/server/status`, {
      credentials: base ? 'include' : 'same-origin',
    });
    if (response.ok) status.value = normalizeServerStatus(await response.json());
  } catch {
    status.value = normalizeServerStatus();
  }
});

onUnmounted(() => window.removeEventListener('arma3:status', applyStatus));
</script>

<template>
  <div id="server-status-badge" :aria-label="`Server ${presentation.label}`">
    <span class="status-dot" :class="presentation.tone"></span>
    <span id="status-text">{{ presentation.label }}</span>
  </div>
</template>
