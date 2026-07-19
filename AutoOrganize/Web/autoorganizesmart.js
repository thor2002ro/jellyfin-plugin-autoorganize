ApiClient.getSmartMatchInfos = function (options) {
    const url = this.getUrl('Library/FileOrganizations/SmartMatches', options || {});

    return this.ajax({
        type: 'GET',
        url: url,
        dataType: 'json'
    });
};

ApiClient.deleteSmartMatchEntries = function (entries) {
    const url = this.getUrl('Library/FileOrganizations/SmartMatches/Delete');

    return this.ajax({
        type: 'POST',
        url: url,
        data: JSON.stringify({ Entries: entries }),
        contentType: 'application/json'
    });
};

const pageSize = 1000;
let currentResult;

function parentWithClass(element, className) {
    while (element && (!element.classList || !element.classList.contains(className))) {
        element = element.parentNode;
    }

    return element || null;
}

async function getAllSmartMatchInfos() {
    const items = [];
    let startIndex = 0;
    let totalRecordCount = 0;

    while (true) {
        const result = await ApiClient.getSmartMatchInfos({
            StartIndex: startIndex,
            Limit: pageSize
        });
        const pageItems = Array.isArray(result?.Items) ? result.Items : [];

        items.push(...pageItems);
        totalRecordCount = Number.isFinite(result?.TotalRecordCount)
            ? result.TotalRecordCount
            : items.length;

        if (pageItems.length === 0 || items.length >= totalRecordCount || pageItems.length < pageSize) {
            break;
        }

        startIndex += pageItems.length;
    }

    return {
        Items: items,
        TotalRecordCount: totalRecordCount
    };
}

function createMatchEntry(infoIndex, matchIndex, matchString) {
    const entry = document.createElement('div');
    entry.className = 'listItem';

    const body = document.createElement('div');
    body.className = 'listItemBody';
    body.style.padding = '.1em 1em .4em 5.5em';
    body.style.minHeight = '1.5em';

    const text = document.createElement('div');
    text.className = 'listItemBodyText secondary';
    text.textContent = matchString || '';
    body.appendChild(text);
    entry.appendChild(body);

    const button = document.createElement('button');
    button.type = 'button';
    button.setAttribute('is', 'emby-button');
    button.className = 'btnDeleteMatchEntry';
    button.style.padding = '0';
    button.dataset.index = String(infoIndex);
    button.dataset.matchindex = String(matchIndex);
    button.title = 'Delete';

    const icon = document.createElement('span');
    icon.className = 'material-icons delete';
    icon.textContent = 'delete';
    button.appendChild(icon);
    entry.appendChild(button);

    return entry;
}

function populateList(page, result) {
    const infos = (Array.isArray(result?.Items) ? result.Items : []).sort(function (left, right) {
        const leftName = left.OrganizerType + ' ' + (left.DisplayName || left.ItemName || '');
        const rightName = right.OrganizerType + ' ' + (right.DisplayName || right.ItemName || '');
        return leftName.localeCompare(rightName);
    });

    currentResult = {
        Items: infos,
        TotalRecordCount: result?.TotalRecordCount ?? infos.length
    };

    const container = page.querySelector('.divMatchInfos');
    container.replaceChildren();

    if (infos.length === 0) {
        return;
    }

    const list = document.createElement('div');
    list.className = 'paperList';

    infos.forEach(function (info, infoIndex) {
        const heading = document.createElement('div');
        heading.className = 'listItem';

        const iconContainer = document.createElement('div');
        iconContainer.className = 'listItemIconContainer';
        const icon = document.createElement('span');
        icon.className = 'listItemIcon material-icons folder';
        icon.textContent = 'folder';
        iconContainer.appendChild(icon);
        heading.appendChild(iconContainer);

        const body = document.createElement('div');
        body.className = 'listItemBody';
        const title = document.createElement('h2');
        title.className = 'listItemBodyText';
        title.textContent = info.DisplayName || info.ItemName || '';
        body.appendChild(title);
        heading.appendChild(body);
        list.appendChild(heading);

        const matchStrings = Array.isArray(info.MatchStrings) ? info.MatchStrings : [];
        matchStrings.forEach(function (matchString, matchIndex) {
            list.appendChild(createMatchEntry(infoIndex, matchIndex, matchString));
        });
    });

    container.appendChild(list);
}

async function reloadList(page) {
    Loading.show();

    try {
        const result = await getAllSmartMatchInfos();
        populateList(page, result);
    } catch (error) {
        Dashboard.processErrorResponse(error);
    } finally {
        Loading.hide();
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

export default function (view) {
    view.querySelector('.divMatchInfos').addEventListener('click', function (event) {
        const button = parentWithClass(event.target, 'btnDeleteMatchEntry');

        if (!button || !currentResult) {
            return;
        }

        const index = Number.parseInt(button.dataset.index, 10);
        const matchIndex = Number.parseInt(button.dataset.matchindex, 10);
        const info = currentResult.Items[index];
        const matchString = info?.MatchStrings?.[matchIndex];

        if (!info?.Id || !matchString) {
            return;
        }

        Loading.show();
        ApiClient.deleteSmartMatchEntries([
            {
                Name: info.Id,
                Value: matchString
            }
        ]).then(function () {
            return reloadList(view);
        }, function (error) {
            Loading.hide();
            Dashboard.processErrorResponse(error);
        });
    });

    view.addEventListener('viewshow', function () {
        LibraryMenu.setTabs('autoorganize', 3, getTabs);
        reloadList(view);
    });

    view.addEventListener('viewhide', function () {
        currentResult = null;
    });
}
