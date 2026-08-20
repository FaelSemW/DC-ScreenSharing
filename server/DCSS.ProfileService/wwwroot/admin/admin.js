// ==========================================================================
// DC-ScreenSharing — Admin Management Frontend Logic
// Full Dual-Protocol Support: WireGuard + OpenVPN (.ovpn)
// ==========================================================================

let currentActiveSection = 'dashboard';
let currentKeysList = [];
let currentClientsList = [];
let currentServersList = [];
let currentCredentialSetsList = [];
let currentProtocolTab = 'WIREGUARD';
let keyFilterMode = 'all';
let validationDebounceTimer = null;
let bulkFilesToImport = [];
let currentPublicationStatus = null;

const sectionTitles = {
    'dashboard': 'Dashboard',
    'access-keys': 'Access Keys',
    'clients': 'Enrolled Clients',
    'servers': 'Servers (Dual Protocol)',
    'generations': 'Catalog Generations',
    'audit': 'Security & Audit Log',
    'system': 'System Information'
};

document.addEventListener('DOMContentLoaded', async () => {
    setupDragAndDrop();
    await checkSession();
});

window.addEventListener('hashchange', handleHashNavigation);

function handleHashNavigation() {
    const hash = window.location.hash.replace('#', '') || 'dashboard';
    navigateTo(hash);
}

function navigateTo(sectionId, event) {
    if (event) event.preventDefault();

    const validSections = ['dashboard', 'access-keys', 'clients', 'servers', 'generations', 'audit', 'system'];
    if (!validSections.includes(sectionId)) sectionId = 'dashboard';

    currentActiveSection = sectionId;
    window.location.hash = sectionId;

    // Update page title in header
    const titleEl = document.getElementById('page-title');
    if (titleEl) {
        titleEl.textContent = sectionTitles[sectionId] || 'Dashboard';
    }

    // Toggle section visibility
    validSections.forEach(s => {
        const el = document.getElementById(`section-${s}`);
        if (el) el.classList.toggle('hidden', s !== sectionId);
    });

    // Update navigation active states
    document.querySelectorAll('.sidebar-nav .nav-item, .bottom-nav .bottom-nav-item').forEach(item => {
        const href = item.getAttribute('href')?.replace('#', '');
        item.classList.toggle('active', href === sectionId);
    });

    // Load data for section
    try {
        switch (sectionId) {
            case 'dashboard': loadDashboard(); break;
            case 'access-keys': loadAccessKeys(); break;
            case 'clients': loadClients(); break;
            case 'servers': loadServers(); break;
            case 'generations': loadGenerations(); break;
            case 'audit': loadAuditLog(); break;
            case 'system': loadSystemInfo(); break;
        }
    } catch (e) {
        console.error(`Error loading section ${sectionId}:`, e);
    }
}

// ==========================================================================
// AUTHENTICATION & SESSION
// ==========================================================================

async function checkSession() {
    try {
        const res = await fetch('/api/v1/admin/auth/session');
        const data = await res.json();

        if (data.authenticated) {
            document.getElementById('login-view')?.classList.add('hidden');
            document.getElementById('admin-view')?.classList.remove('hidden');
            handleHashNavigation();
        } else {
            showLoginView();
        }
    } catch (err) {
        showLoginView();
    }
}

function showLoginView() {
    document.getElementById('admin-view')?.classList.add('hidden');
    document.getElementById('login-view')?.classList.remove('hidden');
}

async function handleLogin(e) {
    e.preventDefault();
    const btn = document.getElementById('login-btn');
    const errBox = document.getElementById('login-error');
    const apiKey = document.getElementById('admin-api-key').value.trim();

    btn.disabled = true;
    btn.textContent = 'Authenticating...';
    errBox.classList.add('hidden');

    try {
        const res = await fetch('/api/v1/admin/auth/login', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ apiKey })
        });

        const data = await res.json();
        if (res.ok && data.success) {
            document.getElementById('admin-api-key').value = '';
            document.getElementById('login-view')?.classList.add('hidden');
            document.getElementById('admin-view')?.classList.remove('hidden');
            showToast('Welcome back, Administrator.', 'success');
            handleHashNavigation();
        } else {
            errBox.textContent = data.error || 'Authentication failed. Please verify your admin access key.';
            errBox.classList.remove('hidden');
        }
    } catch (err) {
        errBox.textContent = 'Connection failed. Please ensure the backend service is reachable.';
        errBox.classList.remove('hidden');
    } finally {
        btn.disabled = false;
        btn.textContent = 'Sign In';
    }
}

async function handleLogout() {
    try {
        await fetch('/api/v1/admin/auth/logout', { method: 'POST' });
    } catch { }
    showLoginView();
    showToast('Signed out successfully.', 'info');
}

function togglePasswordVisibility(inputId) {
    const input = document.getElementById(inputId);
    if (input) input.type = input.type === 'password' ? 'text' : 'password';
}

// ==========================================================================
// API HELPER & TOASTS
// ==========================================================================

async function apiFetch(url, options = {}) {
    const res = await fetch(url, options);
    if (res.status === 401) {
        showLoginView();
        showToast('Your admin session has expired. Please sign in again.', 'danger');
        throw new Error('Session expired.');
    }
    return res;
}

function showToast(message, type = 'info') {
    const container = document.getElementById('toast-container');
    if (!container) return;

    const toast = document.createElement('div');
    toast.className = `toast toast-${type}`;
    toast.innerHTML = `
        <span class="toast-icon">${type === 'success' ? '✓' : type === 'danger' ? '✕' : 'ℹ'}</span>
        <span class="toast-message">${escapeHtml(message)}</span>
    `;

    container.appendChild(toast);

    setTimeout(() => {
        toast.style.opacity = '0';
        toast.style.transform = 'translateY(-10px)';
        setTimeout(() => toast.remove(), 250);
    }, 4000);
}

function escapeHtml(text) {
    if (!text) return '';
    return String(text)
        .replace(/&/g, '&amp;')
        .replace(/</g, '&lt;')
        .replace(/>/g, '&gt;')
        .replace(/"/g, '&quot;')
        .replace(/'/g, '&#039;');
}

function formatDate(dateStr) {
    if (!dateStr) return 'Never';
    const d = new Date(dateStr);
    return isNaN(d.getTime()) ? 'Invalid Date' : d.toLocaleString();
}

// ==========================================================================
// DASHBOARD
// ==========================================================================

async function loadDashboard() {
    const actBox = document.getElementById('dashboard-activations');
    const auditBox = document.getElementById('dashboard-audit');

    try {
        const res = await apiFetch('/api/v1/admin/dashboard');
        const data = await res.json();

        // Update top metrics
        const actClients = document.getElementById('metric-active-clients');
        if (actClients) actClients.textContent = data.activeClientsCount || 0;

        const actKeys = document.getElementById('metric-active-keys');
        if (actKeys) actKeys.textContent = data.activeKeysCount || 0;

        const wgServers = document.getElementById('metric-wg-servers');
        if (wgServers) wgServers.textContent = data.wireGuardServersCount || 0;

        const ovpnServers = document.getElementById('metric-ovpn-servers');
        if (ovpnServers) ovpnServers.textContent = data.openVpnServersCount || 0;

        const genEl = document.getElementById('metric-generation');
        if (genEl) genEl.textContent = `Gen ${data.currentGeneration || 1}`;

        // Render Recent Activations
        if (actBox) {
            if (data.recentActivations && data.recentActivations.length > 0) {
                actBox.innerHTML = `
                    <div class="item-list">
                        ${data.recentActivations.map(c => `
                            <div class="list-row">
                                <div class="row-main">
                                    <div class="row-title-line">
                                        <span class="row-title">${escapeHtml(c.clientId ? c.clientId.substring(0, 12) + '...' : 'Client')}</span>
                                        <span class="badge ${c.isActive ? 'badge-active' : 'badge-revoked'}">${c.isActive ? 'Active' : 'Revoked'}</span>
                                        <span class="badge ${c.accessKeyType === 'GROUP' ? 'badge-group' : 'badge-single'}">${escapeHtml(c.accessKeyType || 'SINGLE')}</span>
                                    </div>
                                    <div class="row-meta">
                                        <span>Key: ${escapeHtml(c.accessKeyName || 'Direct')}</span>
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
        }

        // Render Recent Audit
        if (auditBox) {
            if (data.recentAudit && data.recentAudit.length > 0) {
                auditBox.innerHTML = `
                    <div class="item-list">
                        ${data.recentAudit.map(a => `
                            <div class="list-row">
                                <div class="row-main">
                                    <div class="row-title-line">
                                        <span class="row-title">${escapeHtml(a.action)}</span>
                                        <span class="badge badge-single">${escapeHtml(a.actor)}</span>
                                    </div>
                                    <div class="row-meta">
                                        <span>${formatDate(a.timestampUtc)}</span>
                                        <span>IP: ${escapeHtml(a.clientIp)}</span>
                                    </div>
                                </div>
                            </div>
                        `).join('')}
                    </div>
                `;
            } else {
                auditBox.innerHTML = '<div class="empty-state">No security audit events recorded yet.</div>';
            }
        }
    } catch (e) {
        if (actBox) actBox.innerHTML = '<div class="empty-state">Unable to load dashboard data.<br><button class="btn btn-secondary btn-sm mt-2" onclick="loadDashboard()">Retry</button></div>';
    }
}

// ==========================================================================
// ACCESS KEYS
// ==========================================================================

async function loadAccessKeys() {
    const container = document.getElementById('access-keys-container');
    if (!container) return;
    container.innerHTML = '<div class="loading-state">Loading access keys...</div>';

    try {
        const res = await apiFetch('/api/v1/admin/access-keys');
        currentKeysList = await res.json();
        renderAccessKeys();
    } catch (e) {
        container.innerHTML = '<div class="empty-state">Unable to load access keys.<br><button class="btn btn-secondary btn-sm mt-2" onclick="loadAccessKeys()">Retry</button></div>';
    }
}

function renderAccessKeys() {
    const container = document.getElementById('access-keys-container');
    if (!container) return;

    if (!currentKeysList || currentKeysList.length === 0) {
        container.innerHTML = '<div class="empty-state">No access keys found. Click "Generate Access Key" above to create one.</div>';
        return;
    }

    container.innerHTML = `
        <div class="item-list">
            ${currentKeysList.map(k => `
                <div class="list-row">
                    <div class="row-main">
                        <div class="row-title-line">
                            <span class="row-title">${escapeHtml(k.name)}</span>
                            <span class="badge badge-${(k.status || 'active').toLowerCase()}">${escapeHtml(k.status)}</span>
                            <span class="badge ${k.type === 'GROUP' ? 'badge-group' : 'badge-single'}">${k.type === 'GROUP' ? 'Group Key' : 'Single-Use'}</span>
                        </div>
                        <div class="row-meta">
                            <span>Uses: <strong>${k.useCount} / ${k.maxUses !== null ? k.maxUses : '∞'}</strong></span>
                            <span>Expires: ${formatDate(k.expiresAtUtc)}</span>
                            <span>Created: ${formatDate(k.createdAtUtc)}</span>
                        </div>
                    </div>
                    <div class="row-actions">
                        <button class="btn btn-secondary btn-sm" onclick="showKeyUsage('${k.id}')">Usage</button>
                        ${k.status === 'Active' ? `
                            <button class="btn btn-secondary btn-sm" onclick="disableKey('${k.id}')">Disable</button>
                        ` : k.status === 'Disabled' ? `
                            <button class="btn btn-secondary btn-sm" onclick="enableKey('${k.id}')">Enable</button>
                        ` : ''}
                        ${k.status !== 'Revoked' ? `
                            <button class="btn btn-danger btn-sm" onclick="promptRevokeKey('${k.id}', '${escapeHtml(k.name)}')">Revoke</button>
                        ` : ''}
                    </div>
                </div>
            `).join('')}
        </div>
    `;
}

function openGenerateKeyModal() {
    document.getElementById('generate-key-modal')?.classList.remove('hidden');
    document.getElementById('key-name').value = '';
    document.getElementById('key-type').value = 'SINGLE_USE';
    document.getElementById('key-expiration').value = '30d';
    document.getElementById('key-custom-expiration')?.classList.add('hidden');
    document.getElementById('group-key-options')?.classList.add('hidden');
}

function closeGenerateKeyModal() {
    document.getElementById('generate-key-modal')?.classList.add('hidden');
}

function toggleKeyTypeOptions() {
    const isGroup = document.getElementById('key-type').value === 'GROUP';
    document.getElementById('group-key-options')?.classList.toggle('hidden', !isGroup);
}

function toggleCustomExpiration() {
    const isCustom = document.getElementById('key-expiration').value === 'custom';
    document.getElementById('key-custom-expiration')?.classList.toggle('hidden', !isCustom);
}

async function handleGenerateKey(e) {
    e.preventDefault();
    const btn = document.getElementById('submit-gen-key-btn');
    btn.disabled = true;
    btn.textContent = 'Generating...';

    const name = document.getElementById('key-name').value.trim();
    const type = document.getElementById('key-type').value;
    const expiration = document.getElementById('key-expiration').value;
    const customDate = document.getElementById('key-custom-expiration').value;
    const maxUsesVal = document.getElementById('key-max-uses').value;

    try {
        const res = await apiFetch('/api/v1/admin/access-keys', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({
                name,
                type,
                expiration,
                customExpiresAtUtc: customDate ? new Date(customDate).toISOString() : null,
                maxUses: maxUsesVal ? parseInt(maxUsesVal, 10) : null
            })
        });

        const data = await res.json();
        if (res.ok && data.success) {
            closeGenerateKeyModal();
            showToast(`Key generated: ${data.plaintextCode || data.accessKey}`, 'success');
            loadAccessKeys();
            loadDashboard();
        } else {
            showToast(data.error || 'Failed to generate key.', 'danger');
        }
    } catch (err) {
        showToast('Error generating access key.', 'danger');
    } finally {
        btn.disabled = false;
        btn.textContent = 'Generate Key';
    }
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

async function promptRevokeKey(id, name) {
    if (confirm(`Revoke key "${name}" and all associated clients?`)) {
        try {
            const res = await apiFetch(`/api/v1/admin/access-keys/${id}/revoke`, {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ revokeClients: true })
            });
            if (res.ok) {
                showToast('Key revoked.', 'success');
                loadAccessKeys();
                loadDashboard();
            }
        } catch (e) { }
    }
}

async function showKeyUsage(id) {
    try {
        const res = await apiFetch(`/api/v1/admin/access-keys/${id}/usage`);
        const data = await res.json();
        const modal = document.getElementById('key-usage-modal');
        const body = document.getElementById('key-usage-modal-body');
        document.getElementById('key-usage-modal-title').textContent = `Activations for "${data.key?.name || 'Key'}"`;

        if (!data.clients || data.clients.length === 0) {
            body.innerHTML = '<div class="empty-state">No clients have activated using this key yet.</div>';
        } else {
            body.innerHTML = `
                <div class="item-list">
                    ${data.clients.map(c => `
                        <div class="list-row">
                            <div class="row-main">
                                <div class="row-title-line">
                                    <span class="row-title"><code>${escapeHtml(c.clientId)}</code></span>
                                    <span class="badge ${c.isActive ? 'badge-active' : 'badge-revoked'}">${c.isActive ? 'Active' : 'Revoked'}</span>
                                </div>
                                <div class="row-meta">
                                    <span>IP: ${escapeHtml(c.registeredIp)}</span>
                                    <span>Enrolled: ${formatDate(c.enrolledAtUtc)}</span>
                                </div>
                            </div>
                        </div>
                    `).join('')}
                </div>
            `;
        }
        modal.classList.remove('hidden');
    } catch (e) { }
}

function closeKeyUsageModal() {
    document.getElementById('key-usage-modal')?.classList.add('hidden');
}

// ==========================================================================
// CLIENTS
// ==========================================================================

async function loadClients() {
    const container = document.getElementById('clients-container');
    if (!container) return;
    container.innerHTML = '<div class="loading-state">Loading enrolled clients...</div>';

    try {
        const res = await apiFetch('/api/v1/admin/clients');
        currentClientsList = await res.json();
        filterClients();
    } catch (e) {
        container.innerHTML = '<div class="empty-state">Unable to load enrolled clients.<br><button class="btn btn-secondary btn-sm mt-2" onclick="loadClients()">Retry</button></div>';
    }
}

function filterClients() {
    const container = document.getElementById('clients-container');
    if (!container) return;

    const search = (document.getElementById('client-search')?.value || '').toLowerCase().trim();
    const status = document.getElementById('client-status-filter')?.value || '';

    let filtered = (currentClientsList || []).filter(c => {
        if (status === 'active' && !c.isActive) return false;
        if (status === 'revoked' && c.isActive) return false;
        if (search) {
            const matchId = (c.clientId || '').toLowerCase().includes(search);
            const matchIp = (c.registeredIp || '').toLowerCase().includes(search);
            const matchKey = (c.accessKeyName || '').toLowerCase().includes(search);
            if (!matchId && !matchIp && !matchKey) return false;
        }
        return true;
    });

    if (filtered.length === 0) {
        container.innerHTML = '<div class="empty-state">No enrolled clients match the current filter.</div>';
        return;
    }

    container.innerHTML = `
        <div class="item-list">
            ${filtered.map(c => `
                <div class="list-row">
                    <div class="row-main">
                        <div class="row-title-line">
                            <span class="row-title"><code>${escapeHtml((c.clientId || '').substring(0, 16))}...</code></span>
                            <span class="badge ${c.isActive ? 'badge-active' : 'badge-revoked'}">${c.isActive ? 'Active' : 'Revoked'}</span>
                            <span class="badge badge-single">${escapeHtml(c.accessKeyName || 'Direct Ticket')}</span>
                        </div>
                        <div class="row-meta">
                            <span>IP: ${escapeHtml(c.registeredIp || 'Unknown')}</span>
                            <span>Enrolled: ${formatDate(c.enrolledAtUtc)}</span>
                        </div>
                    </div>
                    <div class="row-actions">
                        ${c.isActive ? `
                            <button class="btn btn-danger btn-sm" onclick="revokeClient('${c.clientId}')">Revoke</button>
                        ` : `
                            <button class="btn btn-secondary btn-sm" onclick="restoreClient('${c.clientId}')">Restore</button>
                        `}
                    </div>
                </div>
            `).join('')}
        </div>
    `;
}

async function revokeClient(clientId) {
    try {
        const res = await apiFetch(`/api/v1/admin/clients/${clientId}/revoke`, { method: 'POST' });
        if (res.ok) {
            showToast('Client revoked.', 'success');
            loadClients();
            loadDashboard();
        }
    } catch (e) { }
}

async function restoreClient(clientId) {
    try {
        const res = await apiFetch(`/api/v1/admin/clients/${clientId}/restore`, { method: 'POST' });
        if (res.ok) {
            showToast('Client restored.', 'success');
            loadClients();
            loadDashboard();
        }
    } catch (e) { }
}

// ==========================================================================
// SERVERS & DUAL-PROTOCOL SUPPORT (WIREGUARD + OPENVPN)
// ==========================================================================

function switchProtocolTab(protocol) {
    currentProtocolTab = protocol;

    document.getElementById('tab-btn-wireguard')?.classList.toggle('active', protocol === 'WIREGUARD');
    document.getElementById('tab-btn-openvpn')?.classList.toggle('active', protocol === 'OPENVPN');

    document.getElementById('wg-toolbar')?.classList.toggle('hidden', protocol !== 'WIREGUARD');
    document.getElementById('ovpn-toolbar')?.classList.toggle('hidden', protocol !== 'OPENVPN');

    renderServers();
}

async function loadServers() {
    const container = document.getElementById('servers-container');
    if (!container) return;
    container.innerHTML = '<div class="loading-state">Loading servers...</div>';

    try {
        const [serversRes, statusRes] = await Promise.all([
            apiFetch('/api/v1/admin/servers'),
            apiFetch('/api/v1/admin/servers/publication-status')
        ]);
        currentServersList = await serversRes.json();
        currentPublicationStatus = await statusRes.json();

        // Update protocol count badges
        const wgCount = (currentServersList || []).filter(s => (s.protocol || 'WIREGUARD').toUpperCase() === 'WIREGUARD').length;
        const ovpnCount = (currentServersList || []).filter(s => (s.protocol || '').toUpperCase() === 'OPENVPN').length;

        const wgBadge = document.getElementById('wg-count-badge');
        if (wgBadge) wgBadge.textContent = wgCount;

        const ovpnBadge = document.getElementById('ovpn-count-badge');
        if (ovpnBadge) ovpnBadge.textContent = ovpnCount;

        // Update Publication Status Banner & Publish Buttons
        const banner = document.getElementById('servers-publish-banner');
        const summaryText = document.getElementById('servers-publish-summary-text');
        const wgPubBtn = document.getElementById('wg-publish-btn');
        const ovpnPubBtn = document.getElementById('ovpn-publish-btn');

        if (currentPublicationStatus && currentPublicationStatus.hasPendingChanges) {
            const pendingCount = (currentPublicationStatus.pendingAdditionsCount || 0) +
                                 (currentPublicationStatus.pendingModificationsCount || 0) +
                                 (currentPublicationStatus.pendingDeletionsCount || 0);
            if (banner) {
                banner.classList.remove('hidden');
                if (summaryText) {
                    const genText = currentPublicationStatus.activeGeneration > 0 ? `Active Generation #${currentPublicationStatus.activeGeneration}` : 'No Active Generation';
                    summaryText.textContent = `${pendingCount} unpublished registry changes differ from ${genText}.`;
                }
            }
            if (wgPubBtn) wgPubBtn.classList.remove('hidden');
            if (ovpnPubBtn) ovpnPubBtn.classList.remove('hidden');
        } else {
            if (banner) banner.classList.add('hidden');
            if (wgPubBtn) wgPubBtn.classList.add('hidden');
            if (ovpnPubBtn) ovpnPubBtn.classList.add('hidden');
        }

        renderServers();
    } catch (e) {
        container.innerHTML = '<div class="empty-state">Unable to load servers registry.<br><button class="btn btn-secondary btn-sm mt-2" onclick="loadServers()">Retry</button></div>';
    }
}

function renderServers() {
    const container = document.getElementById('servers-container');
    if (!container) return;

    const filtered = (currentServersList || []).filter(s => {
        const proto = (s.protocol || 'WIREGUARD').toUpperCase();
        return proto === currentProtocolTab;
    });

    if (filtered.length === 0) {
        const isWg = currentProtocolTab === 'WIREGUARD';
        container.innerHTML = `
            <div class="empty-state">
                No ${isWg ? 'WireGuard (.conf)' : 'OpenVPN (.ovpn)'} servers found in registry.<br>
                Click <strong>"${isWg ? 'Add WireGuard Server' : 'Add OpenVPN Server'}"</strong> above to import.
            </div>
        `;
        return;
    }

    container.innerHTML = `
        <div class="item-list">
            ${filtered.map(s => {
                const isOvpn = (s.protocol || '').toUpperCase() === 'OPENVPN';
                const provider = s.provider || 'Custom';
                const providerBadgeClass = provider.toLowerCase().includes('proton') ? 'badge-proton' :
                                           provider.toLowerCase().includes('vpnbook') ? 'badge-vpnbook' : 'badge-custom';
                const serverId = s.id || s.serverId || 'srv';

                // Publication Status Badge
                let pubBadge = '';
                if (s.publicationStatus === 'PUBLISHED') {
                    pubBadge = `<span class="badge badge-active" title="Included in Active Generation #${s.activeGeneration}">Active Gen #${s.activeGeneration}</span>`;
                } else if (s.publicationStatus === 'PENDING_CHANGES') {
                    pubBadge = `<span class="badge badge-warning" style="background: rgba(245, 158, 11, 0.2); color: #FCD34D; border: 1px solid #F59E0B;" title="Modified since last publication">Unpublished Changes</span>`;
                } else if (s.publicationStatus === 'NOT_PUBLISHED') {
                    pubBadge = `<span class="badge badge-warning" style="background: rgba(245, 158, 11, 0.2); color: #FCD34D; border: 1px solid #F59E0B;" title="Not included in active generation">Not in Active Gen</span>`;
                } else {
                    pubBadge = `<span class="badge badge-expired">Disabled</span>`;
                }

                return `
                    <div class="list-row">
                        <div class="row-main">
                            <div class="row-title-line">
                                <span class="row-title">${escapeHtml(s.name)}</span>
                                <span class="badge ${s.enabled ? 'badge-active' : 'badge-expired'}">${s.enabled ? 'Enabled' : 'Disabled'}</span>
                                ${pubBadge}
                                <span class="badge ${isOvpn ? 'badge-warning' : 'badge-accent'}">${isOvpn ? '🔒 OpenVPN' : '⚡ WireGuard'}</span>
                                <span class="badge ${providerBadgeClass}">${escapeHtml(provider)}</span>
                                <span class="badge badge-single">${escapeHtml(s.countryCode || s.country || 'Global')}</span>
                            </div>
                            <div class="row-meta">
                                <span>ID: <code>${escapeHtml(serverId)}</code></span>
                                <span>Region: <strong>${escapeHtml(s.region || s.country || 'Default')}</strong></span>
                                ${s.city ? `<span>City: ${escapeHtml(s.city)}</span>` : ''}
                                ${s.credentialSetId ? `<span class="badge badge-credset">Linked Credentials</span>` : ''}
                            </div>
                        </div>
                        <div class="row-actions">
                            ${s.enabled ? `
                                <button class="btn btn-secondary btn-sm" onclick="disableServer('${serverId}')">Disable</button>
                            ` : `
                                <button class="btn btn-secondary btn-sm" onclick="enableServer('${serverId}')">Enable</button>
                            `}
                            <button class="btn btn-danger btn-sm" onclick="promptDeleteServer('${serverId}', '${escapeHtml(s.name)}')">Delete</button>
                        </div>
                    </div>
                `;
            }).join('')}
        </div>
    `;
}

// ----------------------------------------------------
// WIREGUARD SERVER MODAL
// ----------------------------------------------------

function openAddServerModal() {
    document.getElementById('add-server-modal')?.classList.remove('hidden');
    document.getElementById('server-display-name').value = '';
    document.getElementById('server-country').value = 'US';
    document.getElementById('server-region').value = '';
    document.getElementById('server-provider').value = 'Custom';
    document.getElementById('server-conf-text').value = '';
    document.getElementById('server-conf-file').value = '';
}

function closeAddServerModal() {
    document.getElementById('add-server-modal')?.classList.add('hidden');
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

async function submitWireGuardForm(publishImmediately) {
    const displayName = document.getElementById('server-display-name').value.trim();
    const country = document.getElementById('server-country').value.trim();
    const region = document.getElementById('server-region').value.trim();
    const provider = document.getElementById('server-provider').value.trim() || 'Custom';
    const confContent = document.getElementById('server-conf-text').value.trim();

    if (!displayName || !country || !confContent) {
        showToast('Please fill in all required fields.', 'danger');
        return;
    }

    if (publishImmediately) {
        if (!confirm('Save WireGuard server and immediately compile & publish a new generation to all active clients?')) {
            return;
        }
    }

    const btn = publishImmediately ? document.getElementById('submit-add-server-publish-btn') : document.getElementById('submit-add-server-save-btn');
    if (btn) {
        btn.disabled = true;
        btn.textContent = publishImmediately ? 'Saving & Publishing...' : 'Saving...';
    }

    try {
        const res = await apiFetch('/api/v1/admin/servers', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ displayName, country, region, provider, confContent, publishImmediately })
        });

        const data = await res.json();
        if (res.ok && data.success) {
            closeAddServerModal();
            showToast(data.message || 'WireGuard server added.', 'success');
            loadServers();
            loadGenerations();
            loadDashboard();
        } else {
            showToast(data.error || 'Failed to add server.', 'danger');
        }
    } catch (err) {
        showToast('Error adding WireGuard server.', 'danger');
    } finally {
        if (btn) {
            btn.disabled = false;
            btn.textContent = publishImmediately ? 'Save & Publish' : 'Save Only';
        }
    }
}

// ----------------------------------------------------
// OPENVPN SERVER MODAL
// ----------------------------------------------------

async function openAddOpenVpnModal() {
    document.getElementById('add-openvpn-modal')?.classList.remove('hidden');
    document.getElementById('ovpn-display-name').value = '';
    document.getElementById('ovpn-country').value = '';
    document.getElementById('ovpn-country-code').value = '';
    document.getElementById('ovpn-city').value = '';
    document.getElementById('ovpn-provider').value = 'Custom';
    document.getElementById('ovpn-file-text').value = '';
    document.getElementById('ovpn-validation-box')?.classList.add('hidden');
    document.getElementById('ovpn-inline-creds')?.classList.add('hidden');

    await populateCredentialSetsDropdown('ovpn-credential-set');
}

function closeAddOpenVpnModal() {
    document.getElementById('add-openvpn-modal')?.classList.add('hidden');
}

function handleProviderChange() {
    validateOvpnLive(false);
}

function toggleInlineCredentials() {
    const setVal = document.getElementById('ovpn-credential-set').value;
    const inlineBox = document.getElementById('ovpn-inline-creds');
    if (inlineBox) {
        inlineBox.classList.toggle('hidden', setVal !== '');
    }
}

function handleOvpnFileSelect(e) {
    const file = e.target.files[0];
    if (!file) return;

    const fname = file.name.replace(/\.ovpn$/i, '');
    const parts = fname.split(/[\.-]/);

    if (parts.length > 0) {
        const code = parts[0].toUpperCase();
        if (code.length === 2) {
            document.getElementById('ovpn-country-code').value = code;
        }
    }

    if (fname.toLowerCase().includes('proton')) {
        document.getElementById('ovpn-provider').value = 'Proton';
    } else if (fname.toLowerCase().includes('vpnbook')) {
        document.getElementById('ovpn-provider').value = 'VPNBook';
    }

    if (!document.getElementById('ovpn-display-name').value) {
        document.getElementById('ovpn-display-name').value = fname;
    }

    const reader = new FileReader();
    reader.onload = (event) => {
        document.getElementById('ovpn-file-text').value = event.target.result;
        validateOvpnLive(false);
    };
    reader.readAsText(file);
}

function validateOvpnLive(isManual = false) {
    clearTimeout(validationDebounceTimer);

    const runValidation = async () => {
        const text = document.getElementById('ovpn-file-text')?.value.trim();
        const box = document.getElementById('ovpn-validation-box');
        const grid = document.getElementById('val-grid');
        const title = document.getElementById('val-status-title');
        const errMsg = document.getElementById('val-error-msg');
        const valBtn = document.getElementById('btn-validate-profile');

        if (!text || text.length < 10) {
            if (isManual) {
                showToast('Please enter or upload an OpenVPN (.ovpn) configuration first.', 'danger');
            } else {
                box?.classList.add('hidden');
            }
            return;
        }

        if (valBtn) {
            valBtn.disabled = true;
            valBtn.textContent = 'Validating...';
        }

        const provider = document.getElementById('ovpn-provider')?.value || 'Custom';

        try {
            const res = await apiFetch('/api/v1/admin/openvpn/validate', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ ovpnContent: text, provider })
            });

            const val = await res.json();
            if (box) box.classList.remove('hidden');

            if (val.isValid) {
                if (box) box.className = 'validation-preview valid';
                if (title) title.innerHTML = '<span>✅</span> Safe OpenVPN Profile Validated';
                if (errMsg) errMsg.classList.add('hidden');

                const remotesText = val.additionalRemotesCount > 0 
                    ? `1 Primary + ${val.additionalRemotesCount} Failover` 
                    : '1 Remote Endpoint';

                const ipv6Text = val.hasIPv6 ? 'IPv4 + IPv6' : 'IPv4 Standard';

                if (grid) {
                    grid.innerHTML = `
                        <div class="val-item">
                            <div class="val-label">Transport</div>
                            <div class="val-value"><span class="badge ${val.protocol === 'UDP' ? 'badge-udp' : 'badge-tcp'}">${escapeHtml(val.protocol || 'UDP')}</span></div>
                        </div>
                        <div class="val-item">
                            <div class="val-label">Primary Remote</div>
                            <div class="val-value">${escapeHtml(val.primaryRemote || 'N/A')}</div>
                        </div>
                        <div class="val-item">
                            <div class="val-label">Authentication</div>
                            <div class="val-value">${escapeHtml(val.authType || 'TLS')}</div>
                        </div>
                        <div class="val-item">
                            <div class="val-label">Provider</div>
                            <div class="val-value">${escapeHtml(val.provider || provider)}</div>
                        </div>
                        <div class="val-item">
                            <div class="val-label">Multiple Remotes</div>
                            <div class="val-value">${escapeHtml(remotesText)}</div>
                        </div>
                        <div class="val-item">
                            <div class="val-label">IP Support</div>
                            <div class="val-value">${escapeHtml(ipv6Text)}</div>
                        </div>
                        <div class="val-item">
                            <div class="val-label">Security Policy</div>
                            <div class="val-value text-success">Allowlist Passed</div>
                        </div>
                    `;
                }

                if (isManual) {
                    showToast('OpenVPN configuration validated successfully.', 'success');
                }
            } else {
                if (box) box.className = 'validation-preview invalid';
                if (title) title.innerHTML = '<span>❌</span> Configuration Rejected';
                if (errMsg) {
                    errMsg.textContent = val.error || 'Configuration contains disallowed or dangerous directives.';
                    errMsg.classList.remove('hidden');
                }
                if (grid) grid.innerHTML = '';

                if (isManual) {
                    showToast(val.error || 'Configuration rejected by security policy.', 'danger');
                }
            }
        } catch (e) {
            if (isManual) showToast('Failed to validate profile with server.', 'danger');
        } finally {
            if (valBtn) {
                valBtn.disabled = false;
                valBtn.innerHTML = '<svg class="btn-icon" viewBox="0 0 24 24" width="14" height="14"><path fill="currentColor" d="M9 16.2L4.8 12l-1.4 1.4L9 19 21 7l-1.4-1.4L9 16.2z"/></svg> Validate Profile';
            }
        }
    };

    if (isManual) {
        runValidation();
    } else {
        validationDebounceTimer = setTimeout(runValidation, 300);
    }
}

async function submitOpenVpnForm(publishImmediately) {
    const displayName = document.getElementById('ovpn-display-name').value.trim();
    const country = document.getElementById('ovpn-country').value.trim();
    const countryCode = document.getElementById('ovpn-country-code').value.trim();
    const city = document.getElementById('ovpn-city').value.trim();
    const provider = document.getElementById('ovpn-provider').value;
    const credentialSetId = document.getElementById('ovpn-credential-set').value || null;
    const username = document.getElementById('ovpn-username').value.trim() || null;
    const password = document.getElementById('ovpn-password').value || null;
    const ovpnContent = document.getElementById('ovpn-file-text').value.trim();

    if (!displayName || !country || !countryCode || !ovpnContent) {
        showToast('Please fill in all required fields.', 'danger');
        return;
    }

    if (publishImmediately) {
        if (!confirm('Save OpenVPN server and immediately compile & publish a new generation to all active clients?')) {
            return;
        }
    }

    const btn = publishImmediately ? document.getElementById('submit-add-ovpn-publish-btn') : document.getElementById('submit-add-ovpn-save-btn');
    if (btn) {
        btn.disabled = true;
        btn.textContent = publishImmediately ? 'Validating & Publishing...' : 'Validating & Saving...';
    }

    try {
        const res = await apiFetch('/api/v1/admin/servers/openvpn', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({
                displayName,
                country,
                countryCode,
                region: city || country,
                city,
                provider,
                credentialSetId,
                username,
                password,
                ovpnContent,
                publishImmediately
            })
        });

        const data = await res.json();
        if (res.ok && data.success) {
            closeAddOpenVpnModal();
            showToast(data.message || 'OpenVPN server added.', 'success');
            loadServers();
            loadGenerations();
            loadDashboard();
            switchProtocolTab('OPENVPN');
        } else {
            showToast(data.error || 'Failed to import OpenVPN profile.', 'danger');
        }
    } catch (err) {
        showToast('Error importing OpenVPN profile.', 'danger');
    } finally {
        if (btn) {
            btn.disabled = false;
            btn.textContent = publishImmediately ? 'Save & Publish' : 'Save Only';
        }
    }
}

// ----------------------------------------------------
// BULK OPENVPN IMPORT
// ----------------------------------------------------

function openBulkOpenVpnModal() {
    document.getElementById('bulk-openvpn-modal')?.classList.remove('hidden');
    bulkFilesToImport = [];
    document.getElementById('bulk-files-input').value = '';
    document.getElementById('bulk-preview-container')?.classList.add('hidden');
    populateCredentialSetsDropdown('bulk-cred-set');
}

function closeBulkOpenVpnModal() {
    document.getElementById('bulk-openvpn-modal')?.classList.add('hidden');
}

function handleBulkFilesSelect(e) {
    const files = Array.from(e.target.files || []);
    if (files.length === 0) return;

    bulkFilesToImport = files;
    document.getElementById('bulk-files-count').textContent = files.length;
    const list = document.getElementById('bulk-files-list');

    list.innerHTML = files.map(f => `
        <div class="d-flex justify-content-between align-items-center py-1" style="border-bottom: 1px solid var(--border-subtle); font-size: 13px;">
            <span>📄 <strong>${escapeHtml(f.name)}</strong></span>
            <span class="text-muted">${(f.size / 1024).toFixed(1)} KB</span>
        </div>
    `).join('');

    document.getElementById('bulk-preview-container')?.classList.remove('hidden');
}

async function handleBulkImportOpenVpn(e) {
    e.preventDefault();
    if (bulkFilesToImport.length === 0) {
        showToast('Please select at least one .ovpn file to import.', 'danger');
        return;
    }

    const btn = document.getElementById('submit-bulk-ovpn-btn');
    btn.disabled = true;
    btn.textContent = `Importing ${bulkFilesToImport.length} Profiles...`;

    const provider = document.getElementById('bulk-provider').value;
    const credentialSetId = document.getElementById('bulk-cred-set').value || null;

    const filesPayload = [];
    for (const f of bulkFilesToImport) {
        const text = await f.text();
        filesPayload.push({
            fileName: f.name,
            content: text
        });
    }

    try {
        const res = await apiFetch('/api/v1/admin/servers/openvpn/bulk', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({
                provider,
                credentialSetId,
                files: filesPayload
            })
        });

        const data = await res.json();
        if (res.ok) {
            closeBulkOpenVpnModal();
            showToast(`Bulk Import Complete: ${data.importedCount} added, ${data.rejectedCount} rejected.`, 'success');
            loadServers();
            loadDashboard();
            switchProtocolTab('OPENVPN');
        } else {
            showToast(data.error || 'Bulk import failed.', 'danger');
        }
    } catch {
        showToast('Error executing bulk import.', 'danger');
    } finally {
        btn.disabled = false;
        btn.textContent = 'Import All Profiles';
    }
}

// ----------------------------------------------------
// CREDENTIAL SETS MANAGEMENT
// ----------------------------------------------------

async function openCredentialSetsModal() {
    document.getElementById('credential-sets-modal')?.classList.remove('hidden');
    closeCredSetForm();
    await loadCredentialSets();
}

function closeCredentialSetsModal() {
    document.getElementById('credential-sets-modal')?.classList.add('hidden');
}

async function loadCredentialSets() {
    const container = document.getElementById('cred-sets-list-container');
    if (!container) return;
    container.innerHTML = '<div class="loading-state">Loading credential sets...</div>';

    try {
        const res = await apiFetch('/api/v1/admin/openvpn/credential-sets');
        currentCredentialSetsList = await res.json();
        renderCredentialSetsList();
    } catch {
        container.innerHTML = '<div class="empty-state">Failed to load credential sets.</div>';
    }
}

function renderCredentialSetsList() {
    const container = document.getElementById('cred-sets-list-container');
    if (!container) return;

    if (!currentCredentialSetsList || currentCredentialSetsList.length === 0) {
        container.innerHTML = '<div class="empty-state">No credential sets created yet. Click "New Credential Set" to add one.</div>';
        return;
    }

    container.innerHTML = `
        <div class="item-list">
            ${currentCredentialSetsList.map(cs => `
                <div class="cred-set-card">
                    <div class="cred-set-info">
                        <div class="cred-set-title">
                            <span>🔑</span>
                            <span>${escapeHtml(cs.name)}</span>
                            <span class="badge badge-accent">${escapeHtml(cs.provider)}</span>
                        </div>
                        <div class="cred-set-meta">
                            <span>User: <strong>${escapeHtml(cs.username)}</strong></span>
                            <span>Password: <code>••••••••</code></span>
                            <span>Updated: ${formatDate(cs.updatedAtUtc)}</span>
                        </div>
                    </div>
                    <div class="row-actions">
                        <button class="btn btn-secondary btn-sm" onclick="editCredSet('${cs.id}')">Edit / Rotate</button>
                        <button class="btn btn-danger btn-sm" onclick="deleteCredSet('${cs.id}', '${escapeHtml(cs.name)}')">Delete</button>
                    </div>
                </div>
            `).join('')}
        </div>
    `;
}

function openCreateCredSetForm() {
    document.getElementById('cred-set-editor-title').textContent = 'New Credential Set';
    document.getElementById('cred-set-edit-id').value = '';
    document.getElementById('cred-set-name').value = '';
    document.getElementById('cred-set-provider').value = 'Proton';
    document.getElementById('cred-set-username').value = '';
    document.getElementById('cred-set-password').value = '';
    document.getElementById('cred-set-password').placeholder = 'Enter password';
    document.getElementById('cred-set-editor')?.classList.remove('hidden');
}

function editCredSet(id) {
    const cs = currentCredentialSetsList.find(c => c.id === id);
    if (!cs) return;

    document.getElementById('cred-set-editor-title').textContent = `Edit "${cs.name}"`;
    document.getElementById('cred-set-edit-id').value = cs.id;
    document.getElementById('cred-set-name').value = cs.name;
    document.getElementById('cred-set-provider').value = cs.provider;
    document.getElementById('cred-set-username').value = cs.username;
    document.getElementById('cred-set-password').value = '';
    document.getElementById('cred-set-password').placeholder = 'Leave blank to keep current password';
    document.getElementById('cred-set-editor')?.classList.remove('hidden');
}

function closeCredSetForm() {
    document.getElementById('cred-set-editor')?.classList.add('hidden');
}

async function handleSaveCredSet(e) {
    e.preventDefault();
    const btn = document.getElementById('submit-cred-set-btn');
    btn.disabled = true;
    btn.textContent = 'Saving...';

    const id = document.getElementById('cred-set-edit-id').value;
    const name = document.getElementById('cred-set-name').value.trim();
    const provider = document.getElementById('cred-set-provider').value;
    const username = document.getElementById('cred-set-username').value.trim();
    const password = document.getElementById('cred-set-password').value;

    try {
        let res;
        if (id) {
            res = await apiFetch(`/api/v1/admin/openvpn/credential-sets/${id}`, {
                method: 'PUT',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ name, provider, username, password: password || null })
            });
        } else {
            res = await apiFetch('/api/v1/admin/openvpn/credential-sets', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ name, provider, username, password })
            });
        }

        const data = await res.json();
        if (res.ok && data.success) {
            closeCredSetForm();
            showToast('Credential set saved successfully.', 'success');
            await loadCredentialSets();
        } else {
            showToast(data.error || 'Failed to save credential set.', 'danger');
        }
    } catch {
        showToast('Error saving credential set.', 'danger');
    } finally {
        btn.disabled = false;
        btn.textContent = 'Save Credential Set';
    }
}

async function deleteCredSet(id, name) {
    if (!confirm(`Delete credential set "${name}"?`)) return;

    try {
        const res = await apiFetch(`/api/v1/admin/openvpn/credential-sets/${id}`, { method: 'DELETE' });
        const data = await res.json();
        if (res.ok && data.success) {
            showToast('Credential set deleted.', 'success');
            await loadCredentialSets();
        } else {
            showToast(data.error || 'Cannot delete credential set.', 'danger');
        }
    } catch {
        showToast('Error deleting credential set.', 'danger');
    }
}

async function populateCredentialSetsDropdown(selectId) {
    const sel = document.getElementById(selectId);
    if (!sel) return;

    try {
        const res = await apiFetch('/api/v1/admin/openvpn/credential-sets');
        const list = await res.json();
        sel.innerHTML = '<option value="">None / Inline Certificate Only</option>' +
            list.map(c => `<option value="${c.id}">${escapeHtml(c.name)} (${escapeHtml(c.provider)})</option>`).join('');
    } catch { }
}

async function disableServer(id) {
    try {
        const res = await apiFetch(`/api/v1/admin/servers/${id}/disable`, { method: 'POST' });
        if (res.ok) {
            showToast('Server disabled.', 'success');
            loadServers();
            loadDashboard();
        }
    } catch { }
}

async function enableServer(id) {
    try {
        const res = await apiFetch(`/api/v1/admin/servers/${id}/enable`, { method: 'POST' });
        if (res.ok) {
            showToast('Server enabled.', 'success');
            loadServers();
            loadDashboard();
        }
    } catch { }
}

async function promptDeleteServer(id, name) {
    if (confirm(`Remove server "${name}" from catalog registry?`)) {
        try {
            const res = await apiFetch(`/api/v1/admin/servers/${id}`, { method: 'DELETE' });
            if (res.ok) {
                showToast('Server deleted.', 'success');
                loadServers();
                loadDashboard();
            }
        } catch { }
    }
}

// ==========================================================================
// GENERATIONS
// ==========================================================================

async function loadGenerations() {
    const container = document.getElementById('generations-container');
    const hero = document.getElementById('generations-active-hero');
    const pendingBox = document.getElementById('generations-pending-box');
    const pendingList = document.getElementById('generations-pending-list');

    if (!container) return;
    container.innerHTML = '<div class="loading-state">Loading generations...</div>';

    try {
        const [genRes, statusRes] = await Promise.all([
            apiFetch('/api/v1/admin/generations'),
            apiFetch('/api/v1/admin/servers/publication-status')
        ]);
        const list = await genRes.json();
        const status = await statusRes.json();

        // Update Active Generation Hero Card
        if (hero) {
            if (status.activeGeneration > 0) {
                hero.innerHTML = `
                    <div style="display: flex; justify-content: space-between; align-items: center; flex-wrap: wrap; gap: 12px;">
                        <div>
                            <div style="display: flex; align-items: center; gap: 8px; margin-bottom: 4px;">
                                <span style="font-size: 18px; font-weight: bold; color: #F8FAFC;">Active Generation #${status.activeGeneration}</span>
                                <span class="badge badge-active">ACTIVE</span>
                            </div>
                            <div style="font-size: 12px; color: #94A3B8;">
                                Published: <strong>${status.activePublishedAtUtc ? formatDate(status.activePublishedAtUtc) : 'N/A'}</strong> |
                                Total Active Servers: <strong>${status.activeGenerationCount}</strong>
                            </div>
                        </div>
                        <button class="btn btn-primary btn-sm" onclick="promptPublishChanges()" style="background: #10B981; border-color: #059669;">
                            ⚡ Publish New Generation
                        </button>
                    </div>
                `;
            } else {
                hero.innerHTML = `
                    <div style="display: flex; justify-content: space-between; align-items: center; flex-wrap: wrap; gap: 12px;">
                        <div>
                            <span style="font-size: 16px; font-weight: bold; color: #F8FAFC;">No Active Generation Published</span>
                            <div style="font-size: 12px; color: #FCD34D;">Clients currently receive zero servers. Publish a generation to make servers available.</div>
                        </div>
                        <button class="btn btn-primary btn-sm" onclick="promptPublishChanges()">
                            ⚡ Publish Initial Generation
                        </button>
                    </div>
                `;
            }
        }

        // Update Pending Changes Box
        if (pendingBox && pendingList) {
            if (status.hasPendingChanges) {
                pendingBox.classList.remove('hidden');
                pendingList.innerHTML = (status.pendingChangesSummary || []).map(item => `<div style="padding: 2px 0;">${escapeHtml(item)}</div>`).join('');
            } else {
                pendingBox.classList.add('hidden');
            }
        }

        if (!list || list.length === 0) {
            container.innerHTML = '<div class="empty-state">No signed catalog generations found. Click "Compile & Publish New Generation" to publish.</div>';
            return;
        }

        container.innerHTML = `
            <div class="item-list">
                ${list.map(g => `
                    <div class="list-row">
                        <div class="row-main">
                            <div class="row-title-line">
                                <span class="row-title">Generation #${g.generation}</span>
                                <span class="badge ${g.isActive ? 'badge-active' : 'badge-expired'}">${g.isActive ? 'Active Generation' : 'Archived'}</span>
                                <span class="badge badge-accent">Total: ${g.serverCount}</span>
                                <span class="badge badge-single">WG: ${g.wireguardCount || 0}</span>
                                <span class="badge badge-warning">OVPN: ${g.openVpnCount || 0}</span>
                            </div>
                            <div class="row-meta">
                                <span>Published: ${formatDate(g.publishedAtUtc)}</span>
                                <span>By: ${escapeHtml(g.publishedBy || 'System')}</span>
                            </div>
                        </div>
                        <div class="row-actions">
                            ${!g.isActive ? `
                                <button class="btn btn-secondary btn-sm" onclick="rollbackToGeneration(${g.generation})">Activate / Rollback</button>
                            ` : ''}
                        </div>
                    </div>
                `).join('')}
            </div>
        `;
    } catch (e) {
        container.innerHTML = '<div class="empty-state">Unable to load catalog generations.<br><button class="btn btn-secondary btn-sm mt-2" onclick="loadGenerations()">Retry</button></div>';
    }
}

async function promptPublishChanges() {
    if (!confirm('Publish server changes?\n\nThis will create a new signed profile generation and make it active for compatible clients.')) return;

    try {
        const res = await apiFetch('/api/v1/admin/generations', { method: 'POST' });
        const data = await res.json();
        if (res.ok && data.success) {
            showToast(data.message || `Generation #${data.generation} published successfully.`, 'success');
            loadServers();
            loadGenerations();
            loadDashboard();
        } else {
            showToast(data.error || 'Failed to publish generation.', 'danger');
        }
    } catch {
        showToast('Error publishing generation.', 'danger');
    }
}

async function handleCompileAndPublishGeneration() {
    await promptPublishChanges();
}

async function rollbackToGeneration(genNumber) {
    if (!confirm(`Switch active catalog generation to #${genNumber}?`)) return;

    try {
        const res = await apiFetch(`/api/v1/admin/generations/${genNumber}/publish`, { method: 'POST' });
        const data = await res.json();
        if (res.ok && data.success) {
            showToast(data.message || 'Switched active generation.', 'success');
            loadGenerations();
            loadDashboard();
        }
    } catch { }
}

// ==========================================================================
// AUDIT LOG & SYSTEM INFO
// ==========================================================================

async function loadAuditLog() {
    const container = document.getElementById('audit-container');
    if (!container) return;
    container.innerHTML = '<div class="loading-state">Loading audit events...</div>';

    try {
        const res = await apiFetch('/api/v1/admin/audit?limit=100');
        const list = await res.json();

        if (!list || list.length === 0) {
            container.innerHTML = '<div class="empty-state">No security audit events recorded yet.</div>';
            return;
        }

        container.innerHTML = `
            <div class="item-list">
                ${list.map(a => `
                    <div class="list-row">
                        <div class="row-main">
                            <div class="row-title-line">
                                <span class="row-title">${escapeHtml(a.action)}</span>
                                <span class="badge badge-single">${escapeHtml(a.actor)}</span>
                                ${a.targetId ? `<span>Target: <code>${escapeHtml(a.targetId)}</code></span>` : ''}
                            </div>
                            <div class="row-meta">
                                <span>${formatDate(a.timestampUtc)}</span>
                                <span>IP: ${escapeHtml(a.clientIp)}</span>
                            </div>
                        </div>
                    </div>
                `).join('')}
            </div>
        `;
    } catch (e) {
        container.innerHTML = '<div class="empty-state">Unable to load audit log.<br><button class="btn btn-secondary btn-sm mt-2" onclick="loadAuditLog()">Retry</button></div>';
    }
}

async function loadSystemInfo() {
    const container = document.getElementById('system-container');
    if (!container) return;
    container.innerHTML = '<div class="loading-state">Loading system info...</div>';

    try {
        const res = await apiFetch('/api/v1/admin/system/info');
        const data = await res.json();

        container.innerHTML = `
            <div class="metrics-grid">
                <div class="metric-card">
                    <div class="metric-value text-success">Healthy</div>
                    <div class="metric-label">Service Health</div>
                </div>
                <div class="metric-card">
                    <div class="metric-value">${(data.uptimeSeconds / 3600).toFixed(1)}h</div>
                    <div class="metric-label">Backend Uptime</div>
                </div>
                <div class="metric-card">
                    <div class="metric-value">${(data.memoryBytes / (1024 * 1024)).toFixed(1)} MB</div>
                    <div class="metric-label">Memory Usage</div>
                </div>
                <div class="metric-card">
                    <div class="metric-value">${data.processorCount} Cores</div>
                    <div class="metric-label">CPU Cores</div>
                </div>
            </div>
            <div class="card mt-3 p-3">
                <div class="row-meta" style="flex-direction: column; align-items: flex-start; gap: 6px;">
                    <div><strong>OS:</strong> ${escapeHtml(data.osVersion)} (${data.is64Bit ? '64-bit' : '32-bit'})</div>
                    <div><strong>Runtime:</strong> .NET ${escapeHtml(data.runtime)}</div>
                    <div><strong>Host Machine:</strong> ${escapeHtml(data.machineName)}</div>
                </div>
            </div>
        `;
    } catch (e) {
        container.innerHTML = '<div class="empty-state">Unable to load system info.<br><button class="btn btn-secondary btn-sm mt-2" onclick="loadSystemInfo()">Retry</button></div>';
    }
}

// ----------------------------------------------------
// DRAG & DROP SETUP
// ----------------------------------------------------

function setupDragAndDrop() {
    ['ovpn-dropzone', 'bulk-dropzone'].forEach(id => {
        const dropzone = document.getElementById(id);
        if (!dropzone) return;

        ['dragenter', 'dragover', 'dragleave', 'drop'].forEach(eventName => {
            dropzone.addEventListener(eventName, (e) => {
                e.preventDefault();
                e.stopPropagation();
            }, false);
        });

        ['dragenter', 'dragover'].forEach(eventName => {
            dropzone.addEventListener(eventName, () => dropzone.classList.add('dragover'), false);
        });

        ['dragleave', 'drop'].forEach(eventName => {
            dropzone.addEventListener(eventName, () => dropzone.classList.remove('dragover'), false);
        });

        dropzone.addEventListener('drop', (e) => {
            const dt = e.dataTransfer;
            const files = dt.files;
            if (id === 'ovpn-dropzone' && files.length > 0) {
                const fakeEvent = { target: { files: [files[0]] } };
                handleOvpnFileSelect(fakeEvent);
            } else if (id === 'bulk-dropzone' && files.length > 0) {
                const fakeEvent = { target: { files: files } };
                handleBulkFilesSelect(fakeEvent);
            }
        }, false);
    });
}
