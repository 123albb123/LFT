(() => {
  'use strict';
  const $ = (selector) => document.querySelector(selector);
  const state = { settings: null, files: [], downloads: new Map(), query: '', sort: 'time-desc', xhr: null, source: null, filesRefreshTimer: null, apiReady: false, eventReady: false };
  const elements = {
    connection: $('#connectionBadge'), uploadCard: $('#uploadCard'), uploadHint: $('#uploadHint'),
    choose: $('#chooseButton'), input: $('#fileInput'), drop: $('#dropZone'), panel: $('#transferPanel'),
    name: $('#transferName'), percent: $('#transferPercent'), bar: $('#transferBar'), status: $('#transferStatus'),
    count: $('#fileCount'), total: $('#totalSize'), list: $('#fileList'), empty: $('#emptyState'), refresh: $('#refreshButton'), toast: $('#toast'), search: $('#searchInput'), sort: $('#sortSelect'), cancel: $('#cancelUploadButton'), downloadsCard: $('#downloadsCard'), downloads: $('#downloadList'), reload: $('#reloadButton'), connectionError: $('#connectionError'), connectionErrorText: $('#connectionErrorText')
  };

  const formatBytes = (value) => {
    if (value >= 1073741824) return `${(value / 1073741824).toFixed(2).replace(/\.00$/, '')} GB`;
    if (value >= 1048576) return `${(value / 1048576).toFixed(2).replace(/\.00$/, '')} MB`;
    if (value >= 1024) return `${(value / 1024).toFixed(1).replace(/\.0$/, '')} KB`;
    return `${value} B`;
  };
  const transferId = () => crypto.randomUUID ? crypto.randomUUID() : `${Date.now()}-${Math.random().toString(16).slice(2)}`;
  const fileUrl = (name) => `${location.origin}/${encodeURIComponent(name)}`;
  let toastTimer;
  function toast(message) {
    elements.toast.textContent = message;
    elements.toast.classList.add('show');
    clearTimeout(toastTimer);
    toastTimer = setTimeout(() => elements.toast.classList.remove('show'), 2600);
  }
  function setProgress(name, percent, status = '') {
    elements.panel.hidden = false;
    elements.name.textContent = name;
    elements.percent.textContent = `${Math.max(0, Math.min(100, Math.round(percent)))}%`;
    elements.bar.style.width = `${Math.max(0, Math.min(100, percent))}%`;
    elements.status.textContent = status;
  }
  function renderDownloads() {
    elements.downloads.replaceChildren(); elements.downloadsCard.hidden = state.downloads.size === 0;
    state.downloads.forEach(task => { const row=document.createElement('article'); row.className='file-row'; const main=document.createElement('div'); main.className='file-main'; const text=document.createElement('div'); text.className='file-copy'; const title=document.createElement('strong'); title.textContent=task.fileName; const meta=document.createElement('p'); meta.className='file-meta'; meta.textContent=`${task.status} · ${task.total ? `${formatBytes(task.bytes)} / ${formatBytes(task.total)} · ${Math.round(task.percent || 0)}%` : '等待服务确认'}`; text.append(title,meta); main.append(text); const actions=document.createElement('div'); actions.className='file-actions'; const active=task.phase === 'running' || task.phase === 'unknown'; actions.append(actionButton(active ? '取消并清除' : '清除',()=>clearDownload(task.id, active))); row.append(main,actions); elements.downloads.append(row); });
  }
  function clearDownload(id, confirmActive = false) { const task=state.downloads.get(id); if(confirmActive && !confirm('该下载可能仍在进行，取消并清除任务吗？')) return; task?.iframe?.remove(); if(task?.watchdog) clearTimeout(task.watchdog); if(task?.historyTimer) clearTimeout(task.historyTimer); state.downloads.delete(id); renderDownloads(); }
  function scheduleDownloadCleanup(task) { if(task.historyTimer) clearTimeout(task.historyTimer); task.historyTimer=setTimeout(()=>clearDownload(task.id), 5 * 60 * 1000); }
  async function readError(response) {
    try { return (await response.json()).error || `请求失败（${response.status}）`; }
    catch { return `请求失败（${response.status}）`; }
  }
  async function loadSettings() {
    const response = await fetch('/api/settings', { cache: 'no-store' });
    if (!response.ok) throw new Error(await readError(response));
    state.settings = await response.json();
    const readOnly = state.settings.readOnlyMode;
    elements.uploadCard.hidden = readOnly || !state.settings.allowWebUpload;
    if (readOnly) toast('当前为只读模式，仅可查看和下载');
    elements.uploadHint.textContent = `单文件上限 ${formatBytes(state.settings.maxUploadBytes)} · 同名${state.settings.duplicateBehavior === 'Overwrite' ? '覆盖' : state.settings.duplicateBehavior === 'AutoRename' ? '自动重命名' : '拒绝'}`;
  }
  async function loadFiles() {
    const response = await fetch('/api/files', { cache: 'no-store' });
    if (!response.ok) throw new Error(await readError(response));
    state.files = await response.json();
    renderFiles();
  }
  function renderFiles() {
    elements.list.replaceChildren();
    const files = state.files.filter(file => file.name.toLocaleLowerCase().includes(state.query.toLocaleLowerCase())).sort((a,b) => state.sort === 'name-asc' ? a.name.localeCompare(b.name, 'zh-CN') : state.sort === 'size-desc' ? b.size-a.size : new Date(b.lastModifiedUtc)-new Date(a.lastModifiedUtc));
    elements.count.textContent = files.length;
    elements.total.textContent = formatBytes(files.reduce((sum, file) => sum + file.size, 0));
    elements.empty.hidden = files.length !== 0;
    for (const file of files) {
      const row = document.createElement('article'); row.className = 'file-row';
      const main = document.createElement('div'); main.className = 'file-main';
      const icon = document.createElement('span'); icon.className = 'file-icon';
      icon.textContent = (file.name.split('.').pop() || 'FILE').slice(0, 4);
      const copy = document.createElement('div'); copy.className = 'file-copy';
      const link = document.createElement('a'); link.className = 'file-name'; link.href = file.url; link.target = '_blank'; link.rel = 'noopener'; link.textContent = file.name; link.title = file.name;
      const meta = document.createElement('p'); meta.className = 'file-meta'; meta.textContent = `${formatBytes(file.size)} · ${new Date(file.lastModifiedUtc).toLocaleString()}`;
      copy.append(link, meta); main.append(icon, copy);
      const actions = document.createElement('div'); actions.className = 'file-actions';
      actions.append(actionButton('下载', () => download(file)), actionButton('复制链接', () => copyLink(file.name)));
      if (state.settings?.allowWebDelete && !state.settings?.readOnlyMode) actions.append(actionButton('删除', () => removeFile(file.name), 'danger'));
      row.append(main, actions); elements.list.append(row);
    }
  }
  function actionButton(label, handler, kind = 'ghost') {
    const button = document.createElement('button'); button.type = 'button'; button.className = `button ${kind}`; button.textContent = label; button.addEventListener('click', handler); return button;
  }
  async function copyLink(name) {
    const value = fileUrl(name);
    let input;
    try {
      if (navigator.clipboard?.writeText && window.isSecureContext) await navigator.clipboard.writeText(value);
      else {
        input = document.createElement('textarea'); input.value = value; input.style.position = 'fixed'; input.style.opacity = '0'; document.body.append(input); input.select();
        if (!document.execCommand('copy')) throw new Error('copy failed');
      }
      toast('文件链接已复制');
    } catch { window.prompt('请复制文件链接：', value); }
    finally { input?.remove(); }
  }
  function download(file) {
    const id = transferId();
    const frame = document.createElement('iframe'); frame.hidden = true;
    frame.src = `${fileUrl(file.name)}?download=1&transferId=${encodeURIComponent(id)}`;
    const task = { id, fileName: file.name, iframe: frame, status: '运行中', phase: 'running', bytes: 0, total: 0, percent: 0, lastActivityAt: Date.now(), watchdog: null, historyTimer: null };
    task.watchdog = setTimeout(() => { if (state.downloads.has(id) && task.lastActivityAt === task.startTime) { task.status='浏览器已开始处理下载，暂时无法确认进度'; task.phase='unknown'; task.watchdog=null; renderDownloads(); } }, 20000);
    task.startTime = task.lastActivityAt; state.downloads.set(id, task); renderDownloads();
    document.body.append(frame);
  }
  async function removeFile(name) {
    if (!confirm(`确定删除“${name}”吗？此操作不可撤销。`)) return;
    const response = await fetch(`/api/files/${encodeURIComponent(name)}`, { method: 'DELETE', headers: { 'X-Lan-Transfer': '1' } });
    if (!response.ok) return toast(await readError(response));
    toast('文件已删除'); await loadFiles();
  }
  async function uploadFiles(fileList) {
    if (!state.settings?.allowWebUpload || state.settings.readOnlyMode) { toast('当前不允许上传'); return; }
    const files = Array.from(fileList || []); if (!files.length) return;
    for (const file of files) {
      if (file.size > state.settings.maxUploadBytes) { toast(`${file.name} 超过上传上限`); continue; }
      await uploadOne(file);
    }
    await loadFiles();
  }
  function uploadOne(file) {
    return new Promise((resolve) => {
      const id = transferId(); const form = new FormData(); form.append('file', file, file.name);
      const xhr = new XMLHttpRequest(); state.xhr = xhr; elements.cancel.hidden = false; xhr.open('POST', '/api/files'); xhr.setRequestHeader('X-Lan-Transfer', '1'); xhr.setRequestHeader('X-Transfer-Id', id); xhr.setRequestHeader('X-File-Size', String(file.size));
      xhr.upload.onprogress = (event) => setProgress(file.name, event.lengthComputable ? event.loaded * 100 / event.total : 0, `正在上传 ${formatBytes(event.loaded)} / ${formatBytes(file.size)}`);
      xhr.onload = () => {
        if (xhr.status >= 200 && xhr.status < 300) { setProgress(file.name, 100, '上传完成'); toast(`${file.name} 上传完成`); }
        else { let message = `上传失败（${xhr.status}）`; try { message = JSON.parse(xhr.responseText).error || message; } catch {} setProgress(file.name, 0, message); toast(message); }
        resolve();
      };
      xhr.onerror = () => { setProgress(file.name, 0, '网络连接中断'); toast('上传失败：网络连接中断'); resolve(); };
      xhr.onabort = () => { setProgress(file.name, 0, '上传已取消'); toast('上传已取消'); resolve(); };
      xhr.onloadend = () => { state.xhr = null; elements.cancel.hidden = true; };
      xhr.send(form);
    });
  }
  function connectEvents() {
    state.source?.close();
    state.eventReady = false;
    updateConnectionBadge();
    const source = state.source = new EventSource('/api/events');
    source.onopen = () => { state.eventReady = true; updateConnectionBadge(); };
    source.onerror = () => { state.eventReady = false; updateConnectionBadge(); };
    source.onmessage = async (event) => {
      let message; try { message = JSON.parse(event.data); } catch { return; }
      if (message.type === 'files-changed') { clearTimeout(state.filesRefreshTimer); state.filesRefreshTimer = setTimeout(() => loadFiles().catch(() => {}), 250); }
      if (message.type === 'transfer' && message.data?.direction === 'download' && state.downloads.has(message.data.id)) {
        const value = message.data; const task = state.downloads.get(value.id); task.phase=value.status; task.status=value.status === 'completed' ? '完成' : value.status === 'failed' ? (value.error || '失败') : '运行中'; task.bytes=value.bytes || 0; task.total=value.total || 0; task.percent=value.percent || 0; task.lastActivityAt=Date.now(); if(task.watchdog) { clearTimeout(task.watchdog); task.watchdog=null; } if (value.status === 'completed' || value.status === 'failed') { task.iframe?.remove(); task.iframe=null; scheduleDownloadCleanup(task); } renderDownloads();
      }
    };
  }
  function updateConnectionBadge() { const online=state.apiReady && state.eventReady; elements.connection.textContent=online ? '已连接' : state.apiReady ? '重连中' : '连接异常'; elements.connection.className=`badge ${online ? 'online' : 'offline'}`; }
  elements.choose.addEventListener('click', () => elements.input.click());
  elements.drop.addEventListener('click', () => elements.input.click());
  elements.input.addEventListener('change', () => { uploadFiles(elements.input.files); elements.input.value = ''; });
  elements.refresh.addEventListener('click', () => loadFiles().catch(error => toast(error.message)));
  elements.search.addEventListener('input', () => { state.query = elements.search.value; renderFiles(); });
  elements.sort.addEventListener('change', () => { state.sort = elements.sort.value; renderFiles(); });
  elements.cancel.addEventListener('click', () => state.xhr?.abort());
  for (const eventName of ['dragenter', 'dragover']) elements.drop.addEventListener(eventName, event => { event.preventDefault(); elements.drop.classList.add('dragging'); });
  for (const eventName of ['dragleave', 'drop']) elements.drop.addEventListener(eventName, event => { event.preventDefault(); elements.drop.classList.remove('dragging'); });
  elements.drop.addEventListener('drop', event => uploadFiles(event.dataTransfer.files));
  elements.choose.disabled = true; elements.drop.disabled = true;
  async function initialize() { elements.choose.disabled = true; elements.drop.disabled = true; state.apiReady=false; state.settings=null; elements.connectionError.hidden=true; renderFiles(); updateConnectionBadge(); const errors=[]; try { await loadSettings(); } catch(error) { errors.push(`设置加载失败：${error.message}`); } try { await loadFiles(); } catch(error) { errors.push(`文件列表加载失败：${error.message}`); } state.apiReady=errors.length===0; if(state.apiReady) { const uploadAllowed=state.settings?.allowWebUpload && !state.settings.readOnlyMode; elements.choose.disabled=!uploadAllowed; elements.drop.disabled=!uploadAllowed; elements.connectionError.hidden=true; } else { elements.connectionErrorText.textContent=errors.join('；'); elements.connectionError.hidden=false; toast(errors.join('；')); } renderFiles(); updateConnectionBadge(); }
  async function reconnect() { connectEvents(); await initialize(); }
  elements.reload.addEventListener('click', reconnect);
  initialize();
  connectEvents();
  window.addEventListener('beforeunload', event => { if (state.xhr) { event.preventDefault(); event.returnValue = ''; } });
  window.addEventListener('pagehide', () => { state.source?.close(); state.xhr?.abort(); clearTimeout(state.filesRefreshTimer); clearTimeout(toastTimer); state.downloads.forEach(task => { task.iframe?.remove(); if(task.watchdog) clearTimeout(task.watchdog); if(task.historyTimer) clearTimeout(task.historyTimer); }); state.downloads.clear(); });
})();
