/**
 * DC-ScreenSharing Mobile-First Web Admin Application
 */

// Global State
let currentSection = 'dashboard';
let currentKeysList = [];
let currentClientsList = [];
let currentServersList = [];
let keyFilterMode = 'all';
let clientFilterMode = 'all';
let clientSearchQuery = '';

// DOM Content Loaded
document.addEventListener('DOMContentLoaded', () => {
    checkSession();
    window.addEventListener('hashchange', handleHashChange);
});

// Toast Notifications
function showToast(message, type = 'primary') {
    const container = document.getElementById('toast-container');
    const toast = document.createElement('div');
    toast.className = `toast toast-${type}`;
    toast.textContent = message;
    container.appendChild(toast);

    setTimeout(() => {
        toast.style.opacity = '0';
        toast.style.transform = 'translateY(-10px)';
        toast.style.transition = 'all 0.25s ease';
        setTimeout(() => toast.remove(), 250);
    }, 3500);
}

// Confirmation Modal Helper
function showConfirmDialog(title, message, confirmBtnText, onConfirm, isDanger = true) {
    const modal = document.getElementById('confirm-modal');
    document.getElementById('confirm-modal-title').textContent = title;
    document.getElementById('confirm-modal-body').innerHTML = message;
    
    const confirmBtn = document.getElementById('confirm-modal-btn');
    confirmBtn.textContent = confirmBtnText;
    confirmBtn.className = isDanger ? 'btn btn-danger' : 'btn btn-primary';

    confirmBtn.onclick = async () => {
        closeConfirmModal();
        await onConfirm();
    };

    modal.classList.remove('hidden');
}

function closeConfirmModal() {
    document.getElementById('confirm-modal').classList.add('hidden');
}

// Session & Auth Management
async function checkSession() {
    try {
        const res = await fetch('/api/v1/admin/auth/session');
        const data = await res.json();
        if (data.authenticated) {
            showAdminView();
            handleHashChange();
        } else {
            showLoginView();
        }
    } catch (e) {
        showLoginView();
    }
}

function showLoginView() {
    document.getElementById('login-view').classList.remove('hidden');
    document.getElementById('admin-view').classList.add('hidden');
    document.getElementById('admin-api-key').value = '';
}

function showAdminView() {
    document.getElementById('login-view').classList.add('hidden');
    document.getElementById('admin-view').classList.remove('hidden');
}

function togglePasswordVisibility(inputId) {
    const input = document.getElementById(inputId);
    input.type = input.type === 'password' ? 'text' : 'password';
}

async function handleLogin(e) {
    e.preventDefault();
    const apiKeyInput = document.getElementById('admin-api-key');
    const loginBtn = document.getElementById('login-btn');
    const errorBox = document.getElementById('login-error');

    const apiKey = apiKeyInput.value.trim();
    if (!apiKey) return;

    loginBtn.disabled = true;
    loginBtn.textContent = 'Signing in...';
    errorBox.classList.add('hidden');

    try {
        const res = await fetch('/api/v1/admin/auth/login', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ apiKey })
        });

        const data = await res.json();
        if (res.ok && data.success) {
            apiKeyInput.value = '';
            showAdminView();
            navigateTo('dashboard');
            showToast('Signed in successfully.', 'success');
        } else {
            errorBox.textContent = data.error || 'Invalid administrator credentials.';
            errorBox.classList.remove('hidden');
        }
    } catch (err) {
        errorBox.textContent = 'Network or server error during sign in.';
        errorBox.classList.remove('hidden');
    } finally {
        loginBtn.disabled = false;
        loginBtn.textContent = 'Sign In';
    }
}

async function handleLogout() {
    try {
        await fetch('/api/v1/admin/auth/logout', { method: 'POST' });
    } catch (e) { }
    showToast('Signed out.', 'primary');
    showLoginView();
}

// Navigation
function handleHashChange() {
    const hash = window.location.hash.replace('#', '') || 'dashboard';
    navigateTo(hash);
}

function navigateTo(sectionId, event) {
    if (event) event.preventDefault();
    currentSection = sectionId;
    window.location.hash = sectionId;

    // Update section visibility
    document.querySelectorAll('.page-section').forEach(sec => sec.classList.add('hidden'));
    const targetSec = document.getElementById(`section-${sectionId}`);
    if (targetSec) {
        targetSec.classList.remove('hidden');
    }

    // Update page title
    const titleMap = {
        'dashboard': 'Dashboard',
        'access-keys': 'Access Keys',
        'clients': 'Enrolled Clients',
        'servers': 'Servers & WireGuard',
        'generations': 'Profile Generations',
        'audit': 'Audit Log',
        'system': 'System Health'
    };
    document.getElementById('page-title').textContent = titleMap[sectionId] || 'Dashboard';

    // Update Nav Active State
    document.querySelectorAll('.nav-item').forEach(el => {
        el.classList.toggle('active', el.getAttribute('href') === `#${sectionId}`);
    });
    document.querySelectorAll('.bottom-nav-item').forEach(el => {
        el.classList.toggle('active', el.getAttribute('href') === `#${sectionId}`);
    });

    // Load section data
    loadSectionData(sectionId);
}

function loadSectionData(sectionId) {
    switch (sectionId) {
        case 'dashboard': loadDashboard(); break;
        case 'access-keys': loadAccessKeys(); break;
        case 'clients': loadClients(); break;
        case 'servers': loadServers(); break;
        case 'generations': loadGenerations(); break;
        case 'audit': loadAuditLog(); break;
        case 'system': loadSystemInfo(); break;
    }
}

// API Fetch Helper with Session Expiration Handling
async function apiFetch(url, options = {}) {
    try {
        const res = await fetch(url, options);
        if (res.status === 401) {
            showToast('Session expired. Please sign in again.', 'danger');
            showLoginView();
            throw new Error('Unauthorized');
        }
        return res;
    } catch (e) {
        throw e;
    }
}

// ==========================================================================
// DASHBOARD
// ==========================================================================

async function loadDashboard() {
    try {
        const res = await apiFetch('/api/v1/admin/dashboard');
        const data = await res.json();

        document.getElementById('stat-active-clients').textContent = data.activeClientsCount || 0;
        document.getElementById('stat-active-keys').textContent = data.activeKeysCount || 0;
        document.getElementById('stat-group-keys').textContent = data.groupKeysCount || 0;
        document.getElementById('stat-available-servers').textContent = data.availableServersCount || 0;

        // Render Recent Activations
        const actBox = document.getElementById('dash-recent-activations');
        if (data.recentActivations && data.recentActivations.length > 0) {
            actBox.innerHTML = `
                <div class="item-list">
                    ${data.recentActivations.map(c => `
                        <div class="list-row">
                            <div class="row-main">
                                <div class="row-title-line">
                                    <span class="row-title">${c.clientId.substring(0, 12)}...</span>
                                    <span class="badge ${c.isActive ? 'badge-active' : 'badge-revoked'}">${c.isActive ? 'Active' : 'Revoked'}</span>
                                    <span class="badge ${c.accessKeyType === 'GROUP' ? 'badge-group' : 'badge-single'}">${c.accessKeyType}</span>
                                </div>
                                <div class="row-meta">
                                    <span>Key: ${escapeHtml(c.accessKeyName || 'N/A')}</span>
                                    <span>${formatDate(c.enrolledAtUtc)}</span>
                                </div>
                            </div>
                        </div>
                    `).join('')}
                </div>
            `;
        } else {
            actBox.innerHTML = '<div class="empty-state">No client activations recorded yet.</div>';
        }

        // Render Recent Audit
        const auditBox = document.getElementById('dash-recent-audit');
        if (data.recentAudit && data.recentAudit.length > 0) {
            auditBox.innerHTML = `
                <div class="item-list">
                    ${data.recentAudit.map(a => `
                        <div class="list-row">
                            <div class="row-main">
                                <div class="row-title-line">
                                    <span class="row-title">${formatActionName(a.action)}</span>
                                    <span class="badge badge-single">${a.actor}</span>
                                </div>
                                <div class="row-meta">
                                    <span>${formatDate(a.timestampUtc)}</span>
                                    <span>IP: ${a.clientIp}</span>
                                </div>
                            </div>
                        </div>
                    `).join('')}
                </div>
            `;
        } else {
            auditBox.innerHTML = '<div class="empty-state">No audit events recorded yet.</div>';
        }

    } catch (e) { }
}

// ==========================================================================
// ACCESS KEYS
// ==========================================================================

async function loadAccessKeys() {
    const container = document.getElementById('access-keys-container');
    container.innerHTML = '<div class="loading-state">Loading access keys...</div>';

    try {
        const res = await apiFetch('/api/v1/admin/access-keys');
        currentKeysList = await res.json();
        renderAccessKeys();
    } catch (e) {
        container.innerHTML = '<div class="empty-state">Failed to load access keys.</div>';
    }
}

function filterKeys(mode, btn) {
    keyFilterMode = mode;
    document.querySelectorAll('#section-access-keys .filter-tab').forEach(b => b.classList.remove('active'));
    btn.classList.add('active');
    renderAccessKeys();
}

function renderAccessKeys() {
    const container = document.getElementById('access-keys-container');
    let list = currentKeysList;

    if (keyFilterMode === 'active') {
        list = list.filter(k => k.status === 'Active');
    } else if (keyFilterMode === 'group') {
        list = list.filter(k => k.type === 'GROUP');
    } else if (keyFilterMode === 'single') {
        list = list.filter(k => k.type === 'SINGLE_USE');
    }

    if (list.length === 0) {
        container.innerHTML = '<div class="empty-state">No access keys found matching filter.</div>';
        return;
    }

    container.innerHTML = `
        <div class="item-list">
            ${list.map(k => `
                <div class="list-row">
                    <div class="row-main">
                        <div class="row-title-line">
                            <span class="row-title">${escapeHtml(k.name)}</span>
                            <span class="badge ${getStatusBadgeClass(k.status)}">${k.status}</span>
                            <span class="badge ${k.type === 'GROUP' ? 'badge-group' : 'badge-single'}">${k.type}</span>
                        </div>
                        <div class="row-meta">
                            <span>Activations: <strong>${k.useCount} / ${k.maxUses ? k.maxUses : 'Unlimited'}</strong></span>
                            <span>Expires: <strong>${k.expiresAtUtc ? formatDate(k.expiresAtUtc) : 'Never'}</strong></span>
                            <span>Created: ${formatDate(k.createdAtUtc)}</span>
                        </div>
                    </div>
                    <div class="row-actions">
                        <button class="btn btn-secondary btn-sm" onclick="viewKeyUsage('${k.id}', '${escapeHtml(k.name)}')">Usage (${k.useCount})</button>
                        ${k.status === 'Active' ? `
                            <button class="btn btn-secondary btn-sm" onclick="disableKey('${k.id}')">Disable</button>
                            <button class="btn btn-danger btn-sm" onclick="promptRevokeKey('${k.id}', '${escapeHtml(k.name)}', ${k.useCount})">Revoke</button>
                        ` : (k.status === 'Disabled' ? `
                            <button class="btn btn-secondary btn-sm" onclick="enableKey('${k.id}')">Enable</button>
                            <button class="btn btn-danger btn-sm" onclick="promptRevokeKey('${k.id}', '${escapeHtml(k.name)}', ${k.useCount})">Revoke</button>
                        ` : '')}
                    </div>
                </div>
            `).join('')}
        </div>
    `;
}

function openGenerateKeyModal() {
    document.getElementById('generate-key-modal').classList.remove('hidden');
    document.getElementById('key-name').value = '';
    document.getElementById('key-expiration').value = '30d';
    document.getElementById('key-max-uses').value = 'unlimited';
    toggleKeyTypeFields();
    toggleCustomExpiration();
}

function closeGenerateKeyModal() {
    document.getElementById('generate-key-modal').classList.add('hidden');
}

function toggleKeyTypeFields() {
    const isGroup = document.querySelector('input[name="key-type"]:checked').value === 'GROUP';
    document.getElementById('group-max-uses-group').style.display = isGroup ? 'block' : 'none';
}

function toggleCustomExpiration() {
    const val = document.getElementById('key-expiration').value;
    const customInput = document.getElementById('key-custom-expiration');
    if (val === 'custom') {
        customInput.classList.remove('hidden');
        customInput.required = true;
    } else {
        customInput.classList.add('hidden');
        customInput.required = false;
    }
}

async function handleGenerateKey(e) {
    e.preventDefault();
    const submitBtn = document.getElementById('submit-gen-key-btn');
    submitBtn.disabled = true;
    submitBtn.textContent = 'Generating...';

    const name = document.getElementById('key-name').value.trim();
    const type = document.querySelector('input[name="key-type"]:checked').value;
    const exp = document.getElementById('key-expiration').value;
    const customExp = document.getElementById('key-custom-expiration').value;
    const maxUsesVal = document.getElementById('key-max-uses').value;
    const customMaxUses = document.getElementById('key-custom-max-uses').value;

    let maxUses = null;
    if (type === 'GROUP') {
        if (maxUsesVal === 'custom') maxUses = parseInt(customMaxUses) || null;
        else if (maxUsesVal !== 'unlimited') maxUses = parseInt(maxUsesVal);
    }

    try {
        const res = await apiFetch('/api/v1/admin/access-keys', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({
                name,
                type,
                expiration: exp,
                customExpiresAtUtc: exp === 'custom' && customExp ? new Date(customExp).toISOString() : null,
                maxUses
            })
        });

        const data = await res.json();
        if (res.ok && data.success) {
            closeGenerateKeyModal();
            showKeyCreatedModal(data.accessKey, data.record);
            loadAccessKeys();
            loadDashboard();
        } else {
            showToast(data.error || 'Failed to create access key.', 'danger');
        }
    } catch (err) {
        showToast('Error generating access key.', 'danger');
    } finally {
        submitBtn.disabled = false;
        submitBtn.textContent = 'Generate Key';
    }
}

function showKeyCreatedModal(code, record) {
    document.getElementById('created-key-code').textContent = code;
    document.getElementById('created-key-meta').innerHTML = `
        <div><strong>Name:</strong> ${escapeHtml(record.name)}</div>
        <div><strong>Type:</strong> ${record.type} (${record.maxUses ? record.maxUses + ' Max Activations' : 'Unlimited Activations'})</div>
        <div><strong>Expires:</strong> ${record.expiresAtUtc ? formatDate(record.expiresAtUtc) : 'Never'}</div>
    `;
    document.getElementById('key-created-modal').classList.remove('hidden');
}

function closeKeyCreatedModal() {
    document.getElementById('key-created-modal').classList.add('hidden');
}

function copyCreatedKey() {
    const code = document.getElementById('created-key-code').textContent;
    if (navigator.clipboard && window.isSecureContext) {
        navigator.clipboard.writeText(code).then(() => {
            showToast('Access key copied to clipboard!', 'success');
        }).catch(() => fallbackCopyText(code));
    } else {
        fallbackCopyText(code);
    }
}

function fallbackCopyText(text) {
    const ta = document.createElement('textarea');
    ta.value = text;
    ta.style.position = 'fixed';
    ta.style.top = '0';
    ta.style.left = '0';
    ta.style.opacity = '0';
    document.body.appendChild(ta);
    ta.focus();
    ta.select();
    try {
        document.execCommand('copy');
        showToast('Access key copied to clipboard!', 'success');
    } catch (e) {
        showToast('Please select and copy the code manually.', 'warning');
    }
    document.body.removeChild(ta);
}

async function disableKey(id) {
    try {
        const res = await apiFetch(`/api/v1/admin/access-keys/${id}/disable`, { method: 'POST' });
        if (res.ok) {
            showToast('Access key disabled.', 'success');
            loadAccessKeys();
        }
    } catch (e) { }
}

async function enableKey(id) {
    try {
        const res = await apiFetch(`/api/v1/admin/access-keys/${id}/enable`, { method: 'POST' });
        if (res.ok) {
            showToast('Access key enabled.', 'success');
            loadAccessKeys();
        }
    } catch (e) { }
}

function promptRevokeKey(id, name, useCount) {
    const bodyHtml = `
        <p>Are you sure you want to revoke key <strong>${name}</strong>?</p>
        <p class="mt-2 text-muted">New activations using this key will immediately be rejected.</p>
        ${useCount > 0 ? `
            <div class="mt-3 p-3" style="background: rgba(242, 63, 67, 0.1); border-radius: 8px;">
                <label style="display: flex; align-items: center; gap: 8px; cursor: pointer;">
                    <input type="checkbox" id="revoke-associated-clients-chk">
                    <span style="font-size: 13px; font-weight: 600; color: #ff7b7f;">Also revoke ${useCount} currently enrolled client(s)</span>
                </label>
            </div>
        ` : ''}
    `;

    showConfirmDialog('Revoke Access Key', bodyHtml, 'Revoke Key', async () => {
        const chk = document.getElementById('revoke-associated-clients-chk');
        const revokeClients = chk ? chk.checked : false;

        try {
            const res = await apiFetch(`/api/v1/admin/access-keys/${id}/revoke`, {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ revokeClients })
            });
            const data = await res.json();
            if (res.ok) {
                showToast(data.message || 'Key revoked.', 'success');
                loadAccessKeys();
                loadDashboard();
            }
        } catch (e) { }
    });
}

async function viewKeyUsage(id, name) {
    const modal = document.getElementById('key-usage-modal');
    document.getElementById('key-usage-modal-title').textContent = `Activations for "${name}"`;
    const body = document.getElementById('key-usage-modal-body');
    body.innerHTML = '<div class="loading-state">Loading activations...</div>';
    modal.classList.remove('hidden');

    try {
        const res = await apiFetch(`/api/v1/admin/access-keys/${id}/usage`);
        const data = await res.json();

        if (data.clients && data.clients.length > 0) {
            body.innerHTML = `
                <div class="item-list">
                    ${data.clients.map(c => `
                        <div class="list-row">
                            <div class="row-main">
                                <div class="row-title-line">
                                    <span class="row-title">${c.clientId}</span>
                                    <span class="badge ${c.isActive ? 'badge-active' : 'badge-revoked'}">${c.isActive ? 'Active' : 'Revoked'}</span>
                                </div>
                                <div class="row-meta">
                                    <span>IP: ${c.registeredIp || 'N/A'}</span>
                                    <span>Enrolled: ${formatDate(c.enrolledAtUtc)}</span>
                                    <span>Last Seen: ${c.lastSeenAtUtc ? formatDate(c.lastSeenAtUtc) : 'Never'}</span>
                                </div>
                            </div>
                            <div class="row-actions">
                                ${c.isActive ? `
                                    <button class="btn btn-danger btn-sm" onclick="revokeClient('${c.clientId}', true)">Revoke</button>
                                ` : `
                                    <button class="btn btn-secondary btn-sm" onclick="restoreClient('${c.clientId}', true)">Restore</button>
                                `}
                            </div>
                        </div>
                    `).join('')}
                </div>
            `;
        } else {
            body.innerHTML = '<div class="empty-state">No clients have activated using this key yet.</div>';
        }
    } catch (e) {
        body.innerHTML = '<div class="empty-state">Error loading key activations.</div>';
    }
}

function closeKeyUsageModal() {
    document.getElementById('key-usage-modal').classList.add('hidden');
}

// ==========================================================================
// CLIENTS
// ==========================================================================

async function loadClients() {
    const container = document.getElementById('clients-container');
    container.innerHTML = '<div class="loading-state">Loading enrolled clients...</div>';

    try {
        const res = await apiFetch('/api/v1/admin/clients');
        currentClientsList = await res.json();
        renderClients();
    } catch (e) {
        container.innerHTML = '<div class="empty-state">Failed to load clients.</div>';
    }
}

function handleClientSearch() {
    clientSearchQuery = document.getElementById('client-search-input').value.trim().toLowerCase();
    renderClients();
}

function filterClients(mode, btn) {
    clientFilterMode = mode;
    document.querySelectorAll('#section-clients .filter-tab').forEach(b => b.classList.remove('active'));
    btn.classList.add('active');
    renderClients();
}

function renderClients() {
    const container = document.getElementById('clients-container');
    let list = currentClientsList;

    if (clientFilterMode === 'active') {
        list = list.filter(c => c.isActive);
    } else if (clientFilterMode === 'revoked') {
        list = list.filter(c => !c.isActive);
    }

    if (clientSearchQuery) {
        list = list.filter(c => 
            c.clientId.toLowerCase().includes(clientSearchQuery) ||
            (c.accessKeyName && c.accessKeyName.toLowerCase().includes(clientSearchQuery)) ||
            (c.registeredIp && c.registeredIp.toLowerCase().includes(clientSearchQuery))
        );
    }

    if (list.length === 0) {
        container.innerHTML = '<div class="empty-state">No clients found matching search/filter.</div>';
        return;
    }

    container.innerHTML = `
        <div class="item-list">
            ${list.map(c => `
                <div class="list-row">
                    <div class="row-main">
                        <div class="row-title-line">
                            <span class="row-title" style="font-family: monospace;">${c.clientId}</span>
                            <span class="badge ${c.isActive ? 'badge-active' : 'badge-revoked'}">${c.isActive ? 'Active' : 'Revoked'}</span>
                            <span class="badge ${c.accessKeyType === 'GROUP' ? 'badge-group' : 'badge-single'}">${c.accessKeyType || 'Single'}</span>
                        </div>
                        <div class="row-meta">
                            <span>Key: <strong>${escapeHtml(c.accessKeyName || 'N/A')}</strong></span>
                            <span>IP: ${c.registeredIp || 'N/A'}</span>
                            <span>Enrolled: ${formatDate(c.enrolledAtUtc)}</span>
                            <span>Last Seen: ${c.lastSeenAtUtc ? formatDate(c.lastSeenAtUtc) : 'Never'}</span>
                        </div>
                    </div>
                    <div class="row-actions">
                        ${c.isActive ? `
                            <button class="btn btn-danger btn-sm" onclick="promptRevokeClient('${c.clientId}')">Revoke</button>
                        ` : `
                            <button class="btn btn-secondary btn-sm" onclick="restoreClient('${c.clientId}')">Restore</button>
                        `}
                    </div>
                </div>
            `).join('')}
        </div>
    `;
}

function promptRevokeClient(clientId) {
    showConfirmDialog('Revoke Client', `Are you sure you want to revoke client <code>${clientId}</code>? This device will be denied all VPN access.`, 'Revoke Client', () => revokeClient(clientId));
}

async function revokeClient(clientId, refreshUsageModal = false) {
    try {
        const res = await apiFetch(`/api/v1/admin/clients/${clientId}/revoke`, { method: 'POST' });
        if (res.ok) {
            showToast('Client revoked.', 'success');
            loadClients();
            loadDashboard();
            if (refreshUsageModal) closeKeyUsageModal();
        }
    } catch (e) { }
}

async function restoreClient(clientId, refreshUsageModal = false) {
    try {
        const res = await apiFetch(`/api/v1/admin/clients/${clientId}/restore`, { method: 'POST' });
        if (res.ok) {
            showToast('Client restored.', 'success');
            loadClients();
            loadDashboard();
            if (refreshUsageModal) closeKeyUsageModal();
        }
    } catch (e) { }
}

// ==========================================================================
// SERVERS & WIREGUARD CONFIG IMPORT
// ==========================================================================

async function loadServers() {
    const container = document.getElementById('servers-container');
    container.innerHTML = '<div class="loading-state">Loading servers...</div>';

    try {
        const res = await apiFetch('/api/v1/admin/servers');
        currentServersList = await res.json();
        renderServers();
    } catch (e) {
        container.innerHTML = '<div class="empty-state">Failed to load servers.</div>';
    }
}

function renderServers() {
    const container = document.getElementById('servers-container');
    if (currentServersList.length === 0) {
        container.innerHTML = '<div class="empty-state">No servers in catalog registry. Click "Add Server" to import a WireGuard profile.</div>';
        return;
    }

    container.innerHTML = `
        <div class="item-list">
            ${currentServersList.map(s => `
                <div class="list-row">
                    <div class="row-main">
                        <div class="row-title-line">
                            <span class="row-title">${escapeHtml(s.name)}</span>
                            <span class="badge ${s.enabled ? 'badge-active' : 'badge-expired'}">${s.enabled ? 'Enabled' : 'Disabled'}</span>
                            <span class="badge badge-single">${escapeHtml(s.country || s.region)}</span>
                        </div>
                        <div class="row-meta">
                            <span>ID: <code>${s.serverId}</code></span>
                            <span>Endpoint: <strong>${s.endpoint}:${s.port}</strong></span>
                            <span>Updated: ${formatDate(s.updatedAtUtc)}</span>
                        </div>
                    </div>
                    <div class="row-actions">
                        ${s.enabled ? `
                            <button class="btn btn-secondary btn-sm" onclick="disableServer('${s.serverId}')">Disable</button>
                        ` : `
                            <button class="btn btn-secondary btn-sm" onclick="enableServer('${s.serverId}')">Enable</button>
                        `}
                        <button class="btn btn-danger btn-sm" onclick="promptDeleteServer('${s.serverId}', '${escapeHtml(s.name)}')">Delete</button>
                    </div>
                </div>
            `).join('')}
        </div>
    `;
}

function openAddServerModal() {
    document.getElementById('add-server-modal').classList.remove('hidden');
    document.getElementById('server-display-name').value = '';
    document.getElementById('server-country').value = 'US';
    document.getElementById('server-region').value = '';
    document.getElementById('server-conf-text').value = '';
    document.getElementById('server-conf-file').value = '';
}

function closeAddServerModal() {
    document.getElementById('add-server-modal').classList.add('hidden');
}

function handleConfFileSelect(e) {
    const file = e.target.files[0];
    if (!file) return;

    const reader = new FileReader();
    reader.onload = (event) => {
        document.getElementById('server-conf-text').value = event.target.result;
    };
    reader.readAsText(file);
}

async function handleAddServer(e) {
    e.preventDefault();
    const btn = document.getElementById('submit-add-server-btn');
    btn.disabled = true;
    btn.textContent = 'Parsing & Adding...';

    const displayName = document.getElementById('server-display-name').value.trim();
    const country = document.getElementById('server-country').value.trim();
    const region = document.getElementById('server-region').value.trim();
    const confContent = document.getElementById('server-conf-text').value.trim();

    try {
        const res = await apiFetch('/api/v1/admin/servers', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({
                displayName,
                country,
                region,
                confContent
            })
        });

        const data = await res.json();
        if (res.ok && data.success) {
            closeAddServerModal();
            showToast(data.message, 'success');
            loadServers();
            loadDashboard();
        } else {
            showToast(data.error || 'Failed to add server.', 'danger');
        }
    } catch (err) {
        showToast('Error adding server.', 'danger');
    } finally {
        btn.disabled = false;
        btn.textContent = 'Add to Registry';
    }
}

async function enableServer(id) {
    try {
        const res = await apiFetch(`/api/v1/admin/servers/${id}/enable`, { method: 'POST' });
        if (res.ok) {
            showToast('Server enabled.', 'success');
            loadServers();
        }
    } catch (e) { }
}

async function disableServer(id) {
    try {
        const res = await apiFetch(`/api/v1/admin/servers/${id}/disable`, { method: 'POST' });
        if (res.ok) {
            showToast('Server disabled.', 'success');
            loadServers();
        }
    } catch (e) { }
}

function promptDeleteServer(id, name) {
    showConfirmDialog('Delete Server', `Are you sure you want to remove server <strong>${name}</strong> from catalog registry?`, 'Delete Server', async () => {
        try {
            const res = await apiFetch(`/api/v1/admin/servers/${id}`, { method: 'DELETE' });
            if (res.ok) {
                showToast('Server removed.', 'success');
                loadServers();
            }
        } catch (e) { }
    });
}

// ==========================================================================
// GENERATIONS
// ==========================================================================

async function loadGenerations() {
    const container = document.getElementById('generations-container');
    container.innerHTML = '<div class="loading-state">Loading generation history...</div>';

    try {
        const res = await apiFetch('/api/v1/admin/generations');
        const list = await res.json();

        if (list.length === 0) {
            container.innerHTML = '<div class="empty-state">No signed generations created yet.</div>';
            return;
        }

        container.innerHTML = `
            <div class="item-list">
                ${list.map(g => `
                    <div class="list-row">
                        <div class="row-main">
                            <div class="row-title-line">
                                <span class="row-title">Generation ${g.generation}</span>
                                <span class="badge ${g.isActive ? 'badge-active' : 'badge-expired'}">${g.isActive ? 'Active Catalog' : 'Archived'}</span>
                            </div>
                            <div class="row-meta">
                                <span>Published: <strong>${formatDate(g.publishedAtUtc)}</strong></span>
                                <span>Published By: ${escapeHtml(g.publishedBy)}</span>
                                <span>Servers: <strong>${g.serverCount}</strong></span>
                            </div>
                        </div>
                        <div class="row-actions">
                            ${!g.isActive ? `
                                <button class="btn btn-secondary btn-sm" onclick="promptSwitchGeneration(${g.generation})">Switch to Gen ${g.generation}</button>
                            ` : ''}
                        </div>
                    </div>
                `).join('')}
            </div>
        `;
    } catch (e) {
        container.innerHTML = '<div class="empty-state">Failed to load generation history.</div>';
    }
}

function handleCompileAndPublishGeneration() {
    showConfirmDialog(
        'Compile & Publish Generation',
        '<p>This will compile all enabled servers from the registry, cryptographically sign the catalog manifest, and make it the active production generation for all clients.</p><p class="mt-2 text-muted">Are you ready to publish?</p>',
        'Publish Generation',
        async () => {
            try {
                const res = await apiFetch('/api/v1/admin/generations', { method: 'POST' });
                const data = await res.json();
                if (res.ok && data.success) {
                    showToast(data.message, 'success');
                    loadGenerations();
                    loadDashboard();
                } else {
                    showToast(data.error || 'Failed to publish generation.', 'danger');
                }
            } catch (e) {
                showToast('Error publishing generation.', 'danger');
            }
        },
        false // not danger, primary accent
    );
}

function promptSwitchGeneration(genNumber) {
    showConfirmDialog('Switch Active Generation', `Switch active production catalog generation to <strong>Generation ${genNumber}</strong>?`, 'Switch Generation', async () => {
        try {
            const res = await apiFetch(`/api/v1/admin/generations/${genNumber}/publish`, { method: 'POST' });
            const data = await res.json();
            if (res.ok && data.success) {
                showToast(data.message, 'success');
                loadGenerations();
                loadDashboard();
            }
        } catch (e) { }
    });
}

// ==========================================================================
// AUDIT LOG
// ==========================================================================

async function loadAuditLog() {
    const container = document.getElementById('audit-container');
    container.innerHTML = '<div class="loading-state">Loading audit events...</div>';

    try {
        const res = await apiFetch('/api/v1/admin/audit?limit=100');
        const list = await res.json();

        if (list.length === 0) {
            container.innerHTML = '<div class="empty-state">No audit log records found.</div>';
            return;
        }

        container.innerHTML = `
            <div class="item-list">
                ${list.map(a => `
                    <div class="list-row">
                        <div class="row-main">
                            <div class="row-title-line">
                                <span class="row-title">${formatActionName(a.action)}</span>
                                <span class="badge badge-single">${a.actor}</span>
                                ${a.targetId ? `<span class="badge badge-expired">Target: ${escapeHtml(a.targetId)}</span>` : ''}
                            </div>
                            <div class="row-meta">
                                <span>${formatDate(a.timestampUtc)}</span>
                                <span>IP: ${a.clientIp}</span>
                                ${formatMetadata(a.metadata)}
                            </div>
                        </div>
                    </div>
                `).join('')}
            </div>
        `;
    } catch (e) {
        container.innerHTML = '<div class="empty-state">Failed to load audit log.</div>';
    }
}

// ==========================================================================
// SYSTEM INFO
// ==========================================================================

async function loadSystemInfo() {
    const container = document.getElementById('system-container');
    container.innerHTML = '<div class="loading-state">Loading system health...</div>';

    try {
        const res = await apiFetch('/api/v1/admin/system');
        const data = await res.json();

        container.innerHTML = `
            <div class="system-grid" style="display: grid; grid-template-columns: repeat(auto-fit, minmax(240px, 1fr)); gap: 16px;">
                <div class="stat-card">
                    <div class="stat-data">
                        <span class="stat-value" style="color: var(--accent-success);">${data.health}</span>
                        <span class="stat-label">Backend Health</span>
                    </div>
                </div>
                <div class="stat-card">
                    <div class="stat-data">
                        <span class="stat-value">${data.environment}</span>
                        <span class="stat-label">Deployment Environment</span>
                    </div>
                </div>
                <div class="stat-card">
                    <div class="stat-data">
                        <span class="stat-value">Gen ${data.activeGeneration}</span>
                        <span class="stat-label">Active Catalog Generation</span>
                    </div>
                </div>
                <div class="stat-card">
                    <div class="stat-data">
                        <span class="stat-value">${data.enabledServersCount} / ${data.totalServersCount}</span>
                        <span class="stat-label">Servers (Enabled / Total)</span>
                    </div>
                </div>
                <div class="stat-card">
                    <div class="stat-data">
                        <span class="stat-value">${data.activeClientsCount} / ${data.totalClientsCount}</span>
                        <span class="stat-label">Enrolled Clients (Active / Total)</span>
                    </div>
                </div>
                <div class="stat-card">
                    <div class="stat-data">
                        <span class="stat-value">${data.activeAccessKeysCount} / ${data.totalAccessKeysCount}</span>
                        <span class="stat-label">Access Keys (Active / Total)</span>
                    </div>
                </div>
            </div>

            <div class="mt-4 p-3" style="background: var(--bg-card); border-radius: 8px; border: 1px solid var(--border-color); font-size: 12px; color: var(--text-secondary);">
                <div><strong>Runtime:</strong> ${data.framework}</div>
                <div><strong>Storage:</strong> ${data.storage}</div>
                <div><strong>Server Time:</strong> ${formatDate(data.serverTimeUtc)}</div>
            </div>
        `;
    } catch (e) {
        container.innerHTML = '<div class="empty-state">Failed to load system info.</div>';
    }
}

// ==========================================================================
// Formatting Helpers
// ==========================================================================

function formatDate(isoStr) {
    if (!isoStr) return 'N/A';
    try {
        const d = new Date(isoStr);
        return d.toLocaleDateString() + ' ' + d.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });
    } catch (e) {
        return isoStr;
    }
}

function formatActionName(action) {
    return action.replace(/([A-Z])/g, ' $1').trim();
}

function formatMetadata(meta) {
    if (!meta || Object.keys(meta).length === 0) return '';
    return Object.entries(meta).map(([k, v]) => `<span>${k}: <strong>${escapeHtml(v)}</strong></span>`).join(' ');
}

function getStatusBadgeClass(status) {
    switch (status) {
        case 'Active': return 'badge-active';
        case 'Revoked': return 'badge-revoked';
        case 'Disabled': return 'badge-expired';
        case 'Expired': return 'badge-expired';
        case 'Consumed': return 'badge-consumed';
        case 'Capacity Reached': return 'badge-consumed';
        default: return 'badge-single';
    }
}

function escapeHtml(str) {
    if (!str) return '';
    return String(str)
        .replace(/&/g, '&amp;')
        .replace(/</g, '&lt;')
        .replace(/>/g, '&gt;')
        .replace(/"/g, '&quot;')
        .replace(/'/g, '&#039;');
}
