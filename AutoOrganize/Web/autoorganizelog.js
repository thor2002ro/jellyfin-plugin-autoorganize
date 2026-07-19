ApiClient.getFileOrganizationResults = function (options) {
    const url = this.getUrl('Library/FileOrganizations', options || {});
    return this.getJSON(url);
};

ApiClient.deleteOriginalFileFromOrganizationResult = function (id) {
    const url = this.getUrl('Library/FileOrganizations/' + encodeURIComponent(id) + '/File');

    return this.ajax({
        type: 'DELETE',
        url: url
    });
};

ApiClient.clearOrganizationLog = function () {
    return this.ajax({
        type: 'DELETE',
        url: this.getUrl('Library/FileOrganizations')
    });
};

ApiClient.clearOrganizationCompletedLog = function () {
    return this.ajax({
        type: 'DELETE',
        url: this.getUrl('Library/FileOrganizations/Completed')
    });
};

ApiClient.performOrganization = function (id) {
    const url = this.getUrl('Library/FileOrganizations/' + encodeURIComponent(id) + '/Organize');

    return this.ajax({
        type: 'POST',
        url: url
    });
};

const query = {
    StartIndex: 0,
    Limit: 50
};

let currentResult = { Items: [], TotalRecordCount: 0 };
let pageGlobal;

function escapeHtml(value) {
    return String(value ?? '').replace(/[&<>"']/g, function (character) {
        return {
            '&': '&amp;',
            '<': '&lt;',
            '>': '&gt;',
            '"': '&quot;',
            "'": '&#39;'
        }[character];
    });
}

function parentWithClass(element, className) {
    while (element && (!element.classList || !element.classList.contains(className))) {
        element = element.parentNode;
    }

    return element || null;
}

function findItem(id) {
    return currentResult?.Items?.find(function (item) {
        return item.Id === id;
    }) || null;
}

function getStatusText(item) {
    if (item.IsInProgress) {
        return 'Organizing';
    }

    if (item.Status === 'SkippedExisting') {
        return 'Skipped';
    }

    if (item.Status === 'Failure') {
        return 'Failed';
    }

    if (item.Status === 'Success') {
        return 'Completed';
    }

    return 'Unknown';
}

function getStatusClass(item) {
    if (item.IsInProgress) {
        return 'aoProgress';
    }

    if (item.Status === 'Failure') {
        return 'aoFailure';
    }

    if (item.Status === 'SkippedExisting') {
        return 'aoSkipped';
    }

    return 'aoSuccess';
}

function formatFileSize(bytes) {
    let value = Number(bytes || 0);
    const units = ['B', 'KiB', 'MiB', 'GiB', 'TiB'];
    let unit = 0;

    while (value >= 1024 && unit < units.length - 1) {
        value /= 1024;
        unit++;
    }

    return (unit === 0 ? value.toFixed(0) : value.toFixed(value >= 10 ? 1 : 2)) + ' ' + units[unit];
}

function formatOrganizerType(type) {
    if (type === 'Episode') {
        return 'TV episode';
    }

    return type || 'Unknown';
}

function showStatusMessage(id) {
    const item = findItem(id);
    if (!item) {
        return;
    }

    Dashboard.alert({
        title: getStatusText(item),
        text: item.StatusMessage || 'No additional status information is available.'
    });
}

function deleteOriginalFile(page, id) {
    const item = findItem(id);
    if (!item) {
        return;
    }

    const message = 'The following file will be deleted:<br/><br/>' +
        escapeHtml(item.OriginalPath) +
        '<br/><br/>Are you sure you wish to proceed?';

    Dashboard.confirm(message, 'Delete File').then(async function () {
        Loading.show();

        try {
            await ApiClient.deleteOriginalFileFromOrganizationResult(id);
            await reloadItems(page, false);
        } catch (error) {
            Dashboard.processErrorResponse(error);
        } finally {
            Loading.hide();
        }
    });
}

async function showCorrectionPopup(page, item) {
    try {
        const { default: fileOrganizer } = await import(Dashboard.getConfigurationResourceUrl('FileOrganizerJs'));
        await fileOrganizer.show(item);
        await reloadItems(page, false);
    } catch (error) {
        if (error?.name !== 'AbortError') {
            Dashboard.processErrorResponse(error);
        }
    }
}

function organizeFile(page, id) {
    const item = findItem(id);
    if (!item) {
        return;
    }

    if (!item.TargetPath) {
        showCorrectionPopup(page, item);
        return;
    }

    let message = 'The following file will be moved from:<br/><br/>' +
        escapeHtml(item.OriginalPath) +
        '<br/><br/>To:<br/><br/>' +
        escapeHtml(item.TargetPath);
    const duplicatePaths = Array.isArray(item.DuplicatePaths) ? item.DuplicatePaths : [];

    if (duplicatePaths.length > 0) {
        message += '<br/><br/>The following duplicates will be deleted:<br/><br/>' +
            duplicatePaths.map(escapeHtml).join('<br/>');
    }

    message += '<br/><br/>Are you sure you wish to proceed?';

    Dashboard.confirm(message, 'Organize File').then(async function () {
        Loading.show();

        try {
            await ApiClient.performOrganization(id);
            await reloadItems(page, false);
        } catch (error) {
            Dashboard.processErrorResponse(error);
        } finally {
            Loading.hide();
        }
    });
}

async function reloadItems(page, showSpinner) {
    if (!page) {
        return;
    }

    const table = page.querySelector('.autoorganizetable');
    const errorState = page.querySelector('.aoError');
    table.setAttribute('aria-busy', 'true');
    errorState.classList.add('hide');
    page.querySelector('.aoEmpty').classList.add('hide');
    setRefreshState(page, true);

    if (showSpinner) {
        Loading.show();
    }

    try {
        let result = await ApiClient.getFileOrganizationResults(query);
        const totalRecordCount = result?.TotalRecordCount ?? 0;

        if (totalRecordCount > 0 && query.StartIndex >= totalRecordCount) {
            query.StartIndex = Math.floor((totalRecordCount - 1) / query.Limit) * query.Limit;
            result = await ApiClient.getFileOrganizationResults(query);
        }

        currentResult = {
            Items: Array.isArray(result?.Items) ? result.Items : [],
            TotalRecordCount: result?.TotalRecordCount ?? 0
        };
        renderResults(page, currentResult);
        page.querySelector('.aoLastUpdated').textContent =
            'Updated ' + new Date().toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });
    } catch (error) {
        page.querySelector('.aoErrorMessage').textContent =
            error?.message || 'Check the Jellyfin server connection and try again.';
        errorState.classList.remove('hide');
        page.querySelector('.aoLastUpdated').textContent = 'Refresh failed';
    } finally {
        table.setAttribute('aria-busy', 'false');
        setRefreshState(page, false);
        if (showSpinner) {
            Loading.hide();
        }
    }
}

function setRefreshState(page, busy) {
    const button = page.querySelector('.btnRefreshLog');
    button.disabled = busy;
    button.setAttribute('aria-busy', busy ? 'true' : 'false');
    button.querySelector('.aoRefreshLabel').textContent = busy ? 'Refreshing…' : 'Refresh';
}

function getQueryPagingHtml(options) {
    const startIndex = options.startIndex;
    const limit = options.limit;
    const totalRecordCount = options.totalRecordCount;
    const recordsEnd = Math.min(startIndex + limit, totalRecordCount);
    const showControls = limit < totalRecordCount;
    let html = '<div class="listPaging">';

    if (showControls) {
        const startAtDisplay = totalRecordCount ? startIndex + 1 : 0;
        html += '<span class="listPagingText">' +
            startAtDisplay + '-' + recordsEnd + ' of ' + totalRecordCount +
            '</span><div class="listPagingButtons">';
        html += '<button type="button" is="paper-icon-button-light" class="btnPreviousPage autoSize" ' +
            (startIndex ? '' : 'disabled') +
            ' title="Previous page"><span class="material-icons arrow_back">arrow_back</span></button>';
        html += '<button type="button" is="paper-icon-button-light" class="btnNextPage autoSize" ' +
            (startIndex + limit >= totalRecordCount ? 'disabled' : '') +
            ' title="Next page"><span class="material-icons arrow_forward">arrow_forward</span></button>';
        html += '</div>';
    }

    return html + '</div>';
}

function renderResults(page, result) {
    const rows = result.Items.map(function (item) {
        return '<tr class="detailTableBodyRow detailTableBodyRow-shaded" id="row' +
            escapeHtml(item.Id) + '">' + renderItemRow(item) + '</tr>';
    }).join('');

    page.querySelector('.resultBody').innerHTML = rows;

    const pagingHtml = getQueryPagingHtml({
        startIndex: query.StartIndex,
        limit: query.Limit,
        totalRecordCount: result.TotalRecordCount
    });
    const topPaging = page.querySelector('.listTopPaging');
    const bottomPaging = page.querySelector('.listBottomPaging');
    topPaging.innerHTML = pagingHtml;
    bottomPaging.innerHTML = pagingHtml;

    for (const button of [topPaging.querySelector('.btnNextPage'), bottomPaging.querySelector('.btnNextPage')]) {
        button?.addEventListener('click', function () {
            query.StartIndex += query.Limit;
            reloadItems(page, true);
        });
    }

    for (const button of [topPaging.querySelector('.btnPreviousPage'), bottomPaging.querySelector('.btnPreviousPage')]) {
        button?.addEventListener('click', function () {
            query.StartIndex = Math.max(0, query.StartIndex - query.Limit);
            reloadItems(page, true);
        });
    }

    const hasResults = result.TotalRecordCount > 0;
    page.querySelector('.btnClearLog').classList.toggle('hide', !hasResults);
    page.querySelector('.btnClearCompleted').classList.toggle('hide', !hasResults);
    page.querySelector('.autoorganizetable').classList.toggle('hide', !hasResults);
    page.querySelector('.aoEmpty').classList.toggle('hide', hasResults);
    page.querySelector('.aoError').classList.add('hide');
    updateLogSummary(page, result);
}

function updateLogSummary(page, result) {
    const items = result.Items || [];
    page.querySelector('.aoTotalCount').textContent = Number(result.TotalRecordCount || 0).toLocaleString();
    page.querySelector('.aoSuccessCount').textContent =
        items.filter(item => !item.IsInProgress && item.Status === 'Success').length.toLocaleString();
    page.querySelector('.aoFailureCount').textContent =
        items.filter(item => !item.IsInProgress && item.Status === 'Failure').length.toLocaleString();
    page.querySelector('.aoSkippedCount').textContent =
        items.filter(item => !item.IsInProgress && item.Status === 'SkippedExisting').length.toLocaleString();
}

function getDisplayDate(value) {
    try {
        const date = Dashboard.datetime.parseISO8601Date(value, true);
        return {
            iso: date.toISOString(),
            text: date.toLocaleString()
        };
    } catch {
        return {
            iso: '',
            text: value || ''
        };
    }
}

function renderItemRow(item) {
    const id = escapeHtml(item.Id);
    const fileName = escapeHtml(item.OriginalFileName);
    const originalPath = escapeHtml(item.OriginalPath || item.OriginalFileName);
    const targetPath = escapeHtml(item.TargetPath || '');
    const statusText = getStatusText(item);
    const statusClass = getStatusClass(item);
    const statusMessage = escapeHtml(item.StatusMessage || '');
    const date = getDisplayDate(item.Date);
    const dateAttribute = date.iso ? ' datetime="' + escapeHtml(date.iso) + '"' : '';
    const statusDetails = statusMessage
        ? '<div class="aoStatusMessage" title="' + statusMessage + '">' + statusMessage + '</div>'
        : '';
    let statusHtml;

    if (!item.IsInProgress && statusMessage) {
        statusHtml = '<button is="emby-button" type="button" data-resultid="' + id +
            '" class="btnShowStatusMessage aoStatusBadge ' + statusClass +
            '" aria-label="Show ' + escapeHtml(statusText) + ' details">' + escapeHtml(statusText) + '</button>';
    } else {
        statusHtml = '<span class="aoStatusBadge ' + statusClass + '">' + escapeHtml(statusText) + '</span>';
    }

    let buttons = '';
    if (!item.IsInProgress && item.Status !== 'Success') {
        buttons += '<button type="button" is="paper-icon-button-light" data-resultid="' + id +
            '" class="btnProcessResult organizerButton autoSize" title="Correct and organize" aria-label="Correct and organize ' +
            fileName + '"><span class="material-icons edit">edit</span></button>';
        buttons += '<button type="button" is="paper-icon-button-light" data-resultid="' + id +
            '" class="btnDeleteResult organizerButton autoSize" title="Delete source" aria-label="Delete source ' +
            fileName + '"><span class="material-icons delete">delete</span></button>';
    }

    return '<td class="detailTableBodyCell" data-title="Status">' + statusHtml + statusDetails + '</td>' +
        '<td class="detailTableBodyCell" data-title="When"><time' + dateAttribute + '>' +
            escapeHtml(date.text) + '</time></td>' +
        '<td data-title="Source file" class="detailTableBodyCell fileCell">' +
            '<div class="aoFileName" title="' + originalPath + '">' + fileName + '</div>' +
            '<div class="aoFileMeta">' + escapeHtml(formatOrganizerType(item.Type)) + ' · ' +
                escapeHtml(formatFileSize(item.FileSize)) + '</div>' +
            '<div class="aoFilePath" title="' + originalPath + '">' + originalPath + '</div></td>' +
        '<td data-title="Destination" class="detailTableBodyCell fileCell">' +
            (targetPath || '<span class="aoDestinationEmpty">Not resolved</span>') + '</td>' +
        '<td class="detailTableBodyCell organizerButtonCell" style="white-space:nowrap;">' + buttons + '</td>';
}

function handleItemClick(event) {
    const statusButton = parentWithClass(event.target, 'btnShowStatusMessage');
    if (statusButton) {
        event.preventDefault();
        showStatusMessage(statusButton.dataset.resultid);
        return;
    }

    const organizeButton = parentWithClass(event.target, 'btnProcessResult');
    if (organizeButton) {
        event.preventDefault();
        organizeFile(pageGlobal, organizeButton.dataset.resultid);
        return;
    }

    const deleteButton = parentWithClass(event.target, 'btnDeleteResult');
    if (deleteButton) {
        event.preventDefault();
        deleteOriginalFile(pageGlobal, deleteButton.dataset.resultid);
    }
}

function onServerEvent(event, apiClient, data) {
    if (event.type === 'ScheduledTaskEnded') {
        if (data?.Key === 'AutoOrganize') {
            reloadItems(pageGlobal, false);
        }
    } else if (event.type === 'AutoOrganize_ItemUpdated' && data) {
        updateItemStatus(pageGlobal, data);
    } else {
        reloadItems(pageGlobal, false);
    }
}

function updateItemStatus(page, item) {
    if (!page || !item?.Id) {
        return;
    }

    const index = currentResult.Items.findIndex(function (existing) {
        return existing.Id === item.Id;
    });

    if (index >= 0) {
        currentResult.Items[index] = item;
    }

    const row = page.querySelector('#row' + item.Id);
    if (row) {
        row.innerHTML = renderItemRow(item);
        updateLogSummary(page, currentResult);
        page.querySelector('.aoLastUpdated').textContent = 'Updated just now';
    } else {
        reloadItems(page, false);
    }
}

function getTabs() {
    return [
        {
            href: Dashboard.getPluginUrl('AutoOrganizeLog'),
            name: 'Activity Log'
        },
        {
            href: Dashboard.getPluginUrl('AutoOrganizeTv'),
            name: 'TV'
        },
        {
            href: Dashboard.getPluginUrl('AutoOrganizeMovie'),
            name: 'Movie'
        },
        {
            href: Dashboard.getPluginUrl('AutoOrganizeSmart'),
            name: 'Smart Matches'
        }];
}

async function clearLog(view, completedOnly) {
    Loading.show();

    try {
        if (completedOnly) {
            await ApiClient.clearOrganizationCompletedLog();
        } else {
            await ApiClient.clearOrganizationLog();
        }

        query.StartIndex = 0;
        await reloadItems(view, false);
    } catch (error) {
        Dashboard.processErrorResponse(error);
    } finally {
        Loading.hide();
    }
}

export default function (view) {
    pageGlobal = view;

    view.querySelector('.resultBody').addEventListener('click', handleItemClick);
    view.querySelector('.btnClearLog').addEventListener('click', function () {
        Dashboard.confirm(
            'Clear every activity entry? This does not delete any media files.',
            'Clear Activity Log'
        ).then(function () {
            clearLog(view, false);
        });
    });
    view.querySelector('.btnClearCompleted').addEventListener('click', function () {
        clearLog(view, true);
    });
    view.querySelector('.btnRetryLog').addEventListener('click', function () {
        reloadItems(view, true);
    });
    view.querySelector('.btnRefreshLog').addEventListener('click', function () {
        reloadItems(view, false);
    });

    view.addEventListener('viewshow', function () {
        pageGlobal = view;
        LibraryMenu.setTabs('autoorganize', 0, getTabs);
        reloadItems(view, true);

        Events.on(ServerNotifications, 'AutoOrganize_LogReset', onServerEvent);
        Events.on(ServerNotifications, 'AutoOrganize_ItemUpdated', onServerEvent);
        Events.on(ServerNotifications, 'AutoOrganize_ItemRemoved', onServerEvent);
        Events.on(ServerNotifications, 'AutoOrganize_ItemAdded', onServerEvent);
        Events.on(ServerNotifications, 'ScheduledTaskEnded', onServerEvent);

        TaskButton({
            mode: 'on',
            progressElem: view.querySelector('.organizeProgress'),
            panel: view.querySelector('.organizeTaskPanel'),
            taskKey: 'AutoOrganize',
            button: view.querySelector('.btnOrganize')
        });
    });

    view.addEventListener('viewhide', function () {
        currentResult = { Items: [], TotalRecordCount: 0 };

        Events.off(ServerNotifications, 'AutoOrganize_LogReset', onServerEvent);
        Events.off(ServerNotifications, 'AutoOrganize_ItemUpdated', onServerEvent);
        Events.off(ServerNotifications, 'AutoOrganize_ItemRemoved', onServerEvent);
        Events.off(ServerNotifications, 'AutoOrganize_ItemAdded', onServerEvent);
        Events.off(ServerNotifications, 'ScheduledTaskEnded', onServerEvent);

        TaskButton({
            mode: 'off',
            button: view.querySelector('.btnOrganize')
        });

        pageGlobal = null;
    });
}
