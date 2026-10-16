// Background jobs: follow one through its event stream until it is done or failed.
import { T } from './strings.js';

/**
 * @param {string} jobId
 * @param {(job: object) => void} [onUpdate] called with every change (progress: {step, done, total})
 * @returns {Promise<object>} the finished job (with result); rejects with the job's error
 */
export function followJob(jobId, onUpdate) {
  return new Promise((resolve, reject) => {
    const events = new EventSource(`api/jobs/${jobId}/events`);
    events.onmessage = (e) => {
      const job = JSON.parse(e.data);
      onUpdate?.(job);
      if (job.status === 'done') { events.close(); resolve(job); }
      if (job.status === 'failed') { events.close(); reject(new Error(job.error || T.jobFailed)); }
    };
    events.addEventListener('error', (e) => {
      // A server "error" event carries a message; a dropped connection reconnects by itself.
      if (e.data) { events.close(); reject(new Error(JSON.parse(e.data).error || T.jobFailed)); }
    });
  });
}

/** "AI đọc trang scan 3/7" from a job's progress. */
export function progressText(job) {
  const p = job?.progress;
  if (!p) return job?.status === 'queued' ? 'Đang chờ đến lượt…' : 'Đang xử lý…';
  return p.total ? `${p.step} ${p.done ?? 0}/${p.total}` : p.step;
}
