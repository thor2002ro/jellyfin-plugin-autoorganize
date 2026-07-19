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
    if (item.Status === 'SkippedExisting') {
        return 'Skipped';
    }

    if (item.Status === 'Failure') {
        return 'Failed';
    }

    return item.Status || 'Unknown';
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
    } catch (error) {
        Dashboard.processErrorResponse(error);
    } finally {
        if (showSpinner) {
            Loading.hide();
        }
    }
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
        html += '<span style="vertical-align:middle;">' +
            startAtDisplay + '-' + recordsEnd + ' of ' + totalRecordCount +
            '</span><div style="display:inline-block;">';
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
}

function getDisplayDate(value) {
    try {
        const date = Dashboard.datetime.parseISO8601Date(value, true);
        return Dashboard.datetime.toLocaleDateString(date);
    } catch {
        return value || '';
    }
}

function renderItemRow(item) {
    const id = escapeHtml(item.Id);
    const fileName = escapeHtml(item.OriginalFileName);
    const targetPath = escapeHtml(item.TargetPath || '');
    const status = item.Status;
    const spinnerClass = item.IsInProgress ? 'syncSpinner' : 'syncSpinner hide';
    let sourceHtml;

    if (item.IsInProgress) {
        sourceHtml = '<span style="color:darkorange;">' + fileName + '</span>';
    } else if (status === 'SkippedExisting' || status === 'Failure') {
        const color = status === 'SkippedExisting' ? 'blue' : 'red';
        sourceHtml = '<a is="emby-linkbutton" data-resultid="' + id + '" style="color:' + color +
            ';" href="#" class="button-link btnShowStatusMessage">' + fileName + '</a>';
    } else {
        sourceHtml = '<span style="color:green;">' + fileName + '</span>';
    }

    let buttons = '';
    if (!item.IsInProgress && status !== 'Success') {
        buttons += '<button type="button" is="paper-icon-button-light" data-resultid="' + id +
            '" class="btnProcessResult organizerButton autoSize" title="Organize"><span class="material-icons edit">edit</span></button>';
        buttons += '<button type="button" is="paper-icon-button-light" data-resultid="' + id +
            '" class="btnDeleteResult organizerButton autoSize" title="Delete source"><span class="material-icons delete">delete</span></button>';
    }

    return '<td class="detailTableBodyCell"><img src="css/images/throbber.gif" alt="" class="' +
        spinnerClass + '" style="vertical-align:middle;" /></td>' +
        '<td class="detailTableBodyCell" data-title="Date">' + escapeHtml(getDisplayDate(item.Date)) + '</td>' +
        '<td data-title="Source" class="detailTableBodyCell fileCell">' + sourceHtml + '</td>' +
        '<td data-title="Destination" class="detailTableBodyCell fileCell">' + targetPath + '</td>' +
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
        clearLog(view, false);
    });
    view.querySelector('.btnClearCompleted').addEventListener('click', function () {
        clearLog(view, true);
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
