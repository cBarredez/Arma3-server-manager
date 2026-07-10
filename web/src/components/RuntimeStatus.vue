<script setup>
import { onMounted, onUnmounted, ref } from 'vue';

const running = ref(false);
const busy = ref(false);

function applyStatus(event) {
  running.value = Boolean(event.detail?.running);
  busy.value = Boolean(event.detail?.busy);
}

onMounted(async () => {
  window.addEventListener('arma3:status', applyStatus);
  const base = (window.ARMA3_API_BASE || '').replace(/\/$/, '');
  try {
    const response = await fetch(`${base}/api/server/status`, {
      credentials: base ? 'include' : 'same-origin',
    });
    if (response.ok) running.value = Boolean((await response.json()).running);
  } catch {
    running.value = false;
  }
});

onUnmounted(() => window.removeEventListener('arma3:status', applyStatus));
</script>

<template>
  <div id="server-status-badge" :aria-label="`Server ${running ? 'online' : 'offline'}`">
    <span class="status-dot" :class="busy ? 'busy' : running ? 'online' : 'offline'"></span>
    <span id="status-text">{{ busy ? 'Processing…' : running ? 'Online' : 'Offline' }}</span>
  </div>
</template>
