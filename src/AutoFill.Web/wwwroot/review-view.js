// A run from the history: the same result card as on the main page.
import { followJob, progressText } from './jobs.js';
import { renderRunCard } from './result-card.js';
import { T } from './strings.js';
import { $, api, formatDate, postJson, toast } from './ui.js';

const runId = new URLSearchParams(location.search).get('id');

async function load() {
  const run = await api(`api/runs/${runId}`);
  $('#run-sub').textContent = `${run.recipe.name} · ${formatDate(run.createdAt)} · ${T.runStatus[run.status] ?? run.status}`;
  if (run.status === 'Queued' || run.status === 'Running') {
    $('#progress').hidden = false;
    try {
      await followJob(run.jobId, (job) => { $('#progress-text').textContent = progressText(job); });
    } catch {
      // The card says why it failed.
    }
    $('#progress').hidden = true;
  }
  const recipes = await api('api/recipes');
  await renderRunCard($('#card'), runId, {
    recipes,
    onRefill: async (recipeId) => {
      try {
        const { runId: id } = await postJson('api/runs', { recipeId, sourceFileIds: run.sources.map((s) => s.fileId) });
        location.href = `run.html?id=${id}`;
      } catch (err) {
        toast(err.message, true);
      }
    },
  });
}

try {
  if (!runId) throw new Error('Thiếu mã lượt chạy');
  await load();
} catch (err) {
  if (err.status !== 401) toast(err.message, true);
}
