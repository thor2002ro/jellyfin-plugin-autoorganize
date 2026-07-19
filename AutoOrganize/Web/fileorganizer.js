ApiClient.performEpisodeOrganization = function (id, options) {
    const url = this.getUrl('Library/FileOrganizations/' + encodeURIComponent(id) + '/Episode/Organize');

    return this.ajax({
        type: 'POST',
        url: url,
        data: JSON.stringify(options),
        contentType: 'application/json'
    });
};

ApiClient.performMovieOrganization = function (id, options) {
    const url = this.getUrl('Library/FileOrganizations/' + encodeURIComponent(id) + '/Movie/Organize');

    return this.ajax({
        type: 'POST',
        url: url,
        data: JSON.stringify(options),
        contentType: 'application/json'
    });
};

function getApiErrorMessage(error) {
    const header = error?.headers && typeof error.headers.get === 'function'
        ? error.headers.get('X-Application-Error-Code')
        : null;

    return header || error?.responseJSON?.detail || error?.responseText || error?.message ||
        'Server returned status code ' + (error?.status ?? 'unknown') +
        ' (' + (error?.statusText || 'unknown error') + ').';
}

function showApiFailure(error) {
    Dashboard.alert({
        title: 'Error',
        text: 'Error: ' + getApiErrorMessage(error)
    });
}

function addOption(select, value, text, selected) {
    const option = new Option(text || '', value || '', false, Boolean(selected));
    select.appendChild(option);
    return option;
}

function renderMediaOptions(context, state, selectNewItem) {
    const select = context.querySelector('#selectMedias');
    select.replaceChildren();
    addOption(select, '', '', false);

    for (const media of state.existingMedias) {
        addOption(select, media.Id, media.Name || '', false);
    }

    if (state.currentNewItem) {
        addOption(select, '##NEW##', state.currentNewItem.Name || '', selectNewItem);
    }
}

function renderFolderOptions(context, mediaLocations) {
    const select = context.querySelector('#selectMediaFolder');
    select.replaceChildren();

    if (mediaLocations.length > 1) {
        addOption(select, '', '', false);
    }

    for (const location of mediaLocations) {
        addOption(select, location.value, location.display, false);
    }
}

function initBaseForm(context, item, state) {
    context.querySelector('.inputFile').textContent = item.OriginalFileName || '';
    context.querySelector('#hfResultId').value = item.Id || '';
    state.extractedName = item.ExtractedName || '';
    state.extractedYear = item.ExtractedYear ?? null;
}

async function populateMedias(context, state) {
    const requestVersion = ++state.requestVersion;
    Loading.show();

    try {
        const result = await ApiClient.getItems(ApiClient.getCurrentUserId(), {
            recursive: true,
            includeItemTypes: state.chosenType,
            sortBy: 'SortName'
        });

        if (requestVersion !== state.requestVersion) {
            return;
        }

        state.existingMedias = Array.isArray(result?.Items) ? result.Items : [];
        renderMediaOptions(context, state, false);

        const virtualFolders = await ApiClient.getVirtualFolders();
        if (requestVersion !== state.requestVersion) {
            return;
        }

        const mediaLocations = [];
        const expectedCollectionType = state.chosenType === 'Movie' ? 'movies' : 'tvshows';

        for (const virtualFolder of virtualFolders || []) {
            if (virtualFolder.CollectionType !== expectedCollectionType) {
                continue;
            }

            for (const path of virtualFolder.Locations || []) {
                mediaLocations.push({
                    value: path,
                    display: (virtualFolder.Name || state.chosenType) + ': ' + path
                });
            }
        }

        state.mediaLocationsCount = mediaLocations.length;
        renderFolderOptions(context, mediaLocations);
    } catch (error) {
        if (requestVersion === state.requestVersion) {
            showApiFailure(error);
        }
    } finally {
        Loading.hide();
    }
}

function initMovieForm(context, item, state) {
    initBaseForm(context, item, state);
    state.chosenType = 'Movie';
    populateMedias(context, state);
}

function initEpisodeForm(context, item, state) {
    initBaseForm(context, item, state);
    state.chosenType = 'Series';

    context.querySelector('.fldRemember').classList.toggle(
        'hide',
        !item.ExtractedName || item.ExtractedName.length < 3);
    context.querySelector('#txtSeason').value = item.ExtractedSeasonNumber ?? 0;
    context.querySelector('#txtEpisode').value = item.ExtractedEpisodeNumber ?? '';
    context.querySelector('#txtEndingEpisode').value = item.ExtractedEndingEpisodeNumber ?? '';
    context.querySelector('#chkRememberCorrection').checked = false;

    populateMedias(context, state);
}

function parseEpisodeNumbers(dlg) {
    const seasonNumber = Number.parseInt(dlg.querySelector('#txtSeason').value, 10);
    const episodeNumber = Number.parseInt(dlg.querySelector('#txtEpisode').value, 10);
    const endingValue = dlg.querySelector('#txtEndingEpisode').value.trim();
    const endingEpisodeNumber = endingValue ? Number.parseInt(endingValue, 10) : null;

    if (!Number.isInteger(seasonNumber) || seasonNumber < 0) {
        throw new Error('Season number must be zero or greater.');
    }

    if (!Number.isInteger(episodeNumber) || episodeNumber <= 0) {
        throw new Error('Episode number must be greater than zero.');
    }

    if (endingEpisodeNumber !== null
        && (!Number.isInteger(endingEpisodeNumber) || endingEpisodeNumber < episodeNumber)) {
        throw new Error('Ending episode number cannot be less than the first episode number.');
    }

    return { seasonNumber, episodeNumber, endingEpisodeNumber };
}

async function submitMediaForm(dlg, state) {
    const resultId = dlg.querySelector('#hfResultId').value;
    let mediaId = dlg.querySelector('#selectMedias').value || null;
    let targetFolder = null;
    let newProviderIds = null;
    let newMediaName = null;
    let newMediaYear = null;

    if (mediaId === '##NEW##') {
        if (!state.currentNewItem) {
            Dashboard.alert({ title: 'Error', text: 'Select a valid media item.' });
            return;
        }

        mediaId = null;
        newProviderIds = state.currentNewItem.ProviderIds || null;
        newMediaName = state.currentNewItem.Name || null;
        newMediaYear = state.currentNewItem.ProductionYear ?? null;
        targetFolder = dlg.querySelector('#selectMediaFolder').value || null;
    }

    Loading.show();

    try {
        if (state.chosenType === 'Series') {
            const numbers = parseEpisodeNumbers(dlg);
            await ApiClient.performEpisodeOrganization(resultId, {
                SeriesId: mediaId,
                SeasonNumber: numbers.seasonNumber,
                EpisodeNumber: numbers.episodeNumber,
                EndingEpisodeNumber: numbers.endingEpisodeNumber,
                RememberCorrection: dlg.querySelector('#chkRememberCorrection').checked,
                NewSeriesProviderIds: newProviderIds,
                NewSeriesName: newMediaName,
                NewSeriesYear: newMediaYear,
                TargetFolder: targetFolder
            });
        } else if (state.chosenType === 'Movie') {
            await ApiClient.performMovieOrganization(resultId, {
                MovieId: mediaId,
                NewMovieProviderIds: newProviderIds,
                NewMovieName: newMediaName,
                NewMovieYear: newMediaYear,
                TargetFolder: targetFolder
            });
        } else {
            throw new Error('Select a media type.');
        }

        state.submitted = true;
        Dashboard.dialogHelper.close(dlg);
    } catch (error) {
        showApiFailure(error);
    } finally {
        Loading.hide();
    }
}

async function showNewMediaDialog(dlg, state) {
    if (state.mediaLocationsCount === 0) {
        Dashboard.alert({
            title: 'Error',
            text: 'No compatible ' + (state.chosenType === 'Movie' ? 'movie' : 'TV') +
                ' library root is configured in Jellyfin.'
        });
        return;
    }

    try {
        const newItem = await Dashboard.itemIdentifier.showFindNew(
            state.extractedName,
            state.extractedYear,
            state.chosenType,
            ApiClient.serverId());

        if (newItem) {
            state.currentNewItem = newItem;
            renderMediaOptions(dlg, state, true);
            selectedMediasChanged(dlg);
        }
    } catch (error) {
        showApiFailure(error);
    }
}

function selectedMediasChanged(dlg) {
    const isNewMedia = dlg.querySelector('#selectMedias').value === '##NEW##';
    const folderField = dlg.querySelector('.fldSelectMediaFolder');
    const folderSelect = dlg.querySelector('#selectMediaFolder');

    folderField.classList.toggle('hide', !isNewMedia);
    if (isNewMedia) {
        folderSelect.setAttribute('required', 'required');
    } else {
        folderSelect.removeAttribute('required');
    }
}

function selectedMediaTypeChanged(dlg, item, state) {
    const mediaType = dlg.querySelector('#selectMediaType').value;
    const mediaSelector = dlg.querySelector('#selectMedias');
    const permitChoice = dlg.querySelector('#divPermitChoice');
    const globalChoice = dlg.querySelector('#divGlobalChoice');
    const episodeChoice = dlg.querySelector('#divEpisodeChoice');

    state.currentNewItem = null;
    state.existingMedias = [];
    state.mediaLocationsCount = 0;
    state.requestVersion++;
    selectedMediasChanged(dlg);

    if (mediaType === '') {
        state.chosenType = null;
        permitChoice.classList.add('hide');
        globalChoice.classList.add('hide');
        episodeChoice.classList.add('hide');
        return;
    }

    permitChoice.classList.remove('hide');
    globalChoice.classList.remove('hide');

    if (mediaType === 'Movie') {
        mediaSelector.setAttribute('label', 'Movie');
        if (typeof mediaSelector.setLabel === 'function') {
            mediaSelector.setLabel('Movie');
        }

        episodeChoice.classList.add('hide');
        dlg.querySelector('#txtSeason').removeAttribute('required');
        dlg.querySelector('#txtEpisode').removeAttribute('required');
        initMovieForm(dlg, item, state);
        return;
    }

    mediaSelector.setAttribute('label', 'Series');
    if (typeof mediaSelector.setLabel === 'function') {
        mediaSelector.setLabel('Series');
    }

    episodeChoice.classList.remove('hide');
    dlg.querySelector('#txtSeason').setAttribute('required', 'required');
    dlg.querySelector('#txtEpisode').setAttribute('required', 'required');
    initEpisodeForm(dlg, item, state);
}

export default {
    show: async function (item) {
        const state = {
            chosenType: null,
            extractedName: '',
            extractedYear: null,
            currentNewItem: null,
            existingMedias: [],
            mediaLocationsCount: 0,
            requestVersion: 0,
            submitted: false
        };

        const response = await fetch(Dashboard.getConfigurationResourceUrl('FileOrganizerHtml'));
        if (!response.ok) {
            throw new Error('Unable to load the file organizer dialog.');
        }

        const template = await response.text();
        const dlg = Dashboard.dialogHelper.createDialog({
            removeOnClose: true,
            size: 'small'
        });

        dlg.classList.add('ui-body-a', 'background-theme-a', 'formDialog');
        dlg.innerHTML = template;
        dlg.querySelector('.formDialogHeaderTitle').textContent = 'Organize';

        dlg.querySelector('.btnCancel').addEventListener('click', function () {
            Dashboard.dialogHelper.close(dlg);
        });

        dlg.querySelector('form').addEventListener('submit', function (event) {
            event.preventDefault();
            submitMediaForm(dlg, state);
            return false;
        });

        dlg.querySelector('#btnNewMedia').addEventListener('click', function () {
            showNewMediaDialog(dlg, state);
        });

        dlg.querySelector('#selectMedias').addEventListener('change', function () {
            selectedMediasChanged(dlg);
        });

        dlg.querySelector('#selectMediaType').addEventListener('change', function () {
            selectedMediaTypeChanged(dlg, item, state);
        });

        dlg.querySelector('#selectMediaType').value = item.Type || '';
        selectedMediaTypeChanged(dlg, item, state);
        Dashboard.dialogHelper.open(dlg);

        return new Promise(function (resolve, reject) {
            dlg.addEventListener('close', function () {
                state.requestVersion++;
                if (state.submitted) {
                    resolve();
                } else {
                    reject(new DOMException('File organization was cancelled.', 'AbortError'));
                }
            }, { once: true });
        });
    }
};
