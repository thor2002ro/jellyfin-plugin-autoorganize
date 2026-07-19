function replaceAll(value, token, replacement) {
    return String(value ?? '').split(token).join(replacement);
}

function applyReplacements(value, replacements) {
    let result = String(value ?? '');

    for (const [token, replacement] of replacements) {
        result = replaceAll(result, token, replacement);
    }

    return result;
}

function replaceSpaces(value, replacement) {
    return replaceAll(value, ' ', replacement);
}

function getEpisodeFileName(value, enableMultiEpisode) {
    const seriesName = 'Series Name';
    const episodeTitle = 'Episode Four';
    const fileName = seriesName + ' ' + episodeTitle;
    const replacements = [
        ['%s.n', replaceSpaces(seriesName, '.')],
        ['%s_n', replaceSpaces(seriesName, '_')],
        ['%sn', seriesName],
        ['%e.n', replaceSpaces(episodeTitle, '.')],
        ['%e_n', replaceSpaces(episodeTitle, '_')],
        ['%en', episodeTitle],
        ['%00ed', '005'],
        ['%0ed', '05'],
        ['%ed', '5'],
        ['%00e', '004'],
        ['%0e', '04'],
        ['%e', '4'],
        ['%00s', '001'],
        ['%0s', '01'],
        ['%s', '1'],
        ['%ext', 'mkv'],
        ['%fn', fileName]
    ];

    if (!enableMultiEpisode) {
        replacements.splice(6, 3);
    }

    return applyReplacements(value, replacements);
}

function getSeriesDirectoryName(value) {
    const seriesName = 'Series Name';
    const seriesYear = '2017';
    const fullName = seriesName + ' (' + seriesYear + ')';

    return applyReplacements(value, [
        ['%s.n', replaceSpaces(seriesName, '.')],
        ['%s_n', replaceSpaces(seriesName, '_')],
        ['%sn', seriesName],
        ['%sy', seriesYear],
        ['%fn', fullName]
    ]);
}

function getSeasonDirectoryName(value) {
    return applyReplacements(value, [
        ['%00s', '001'],
        ['%0s', '01'],
        ['%s', '1']
    ]);
}

function getApiErrorMessage(error) {
    const header = error?.headers && typeof error.headers.get === 'function'
        ? error.headers.get('X-Application-Error-Code')
        : null;

    return header || error?.responseJSON?.detail || error?.responseText || error?.message ||
        'Server returned status code ' + (error?.status ?? 'unknown') +
        ' (' + (error?.statusText || 'unknown error') + ').';
}

function normalizeExtensions(value) {
    return String(value ?? '')
        .split(';')
        .map(function (extension) { return extension.trim(); })
        .filter(Boolean);
}

function setSelectOptions(select, options, includeBlank) {
    select.replaceChildren();

    if (includeBlank) {
        select.appendChild(new Option('', ''));
    }

    for (const option of options) {
        select.appendChild(new Option(option.display, option.value));
    }
}

function loadPage(view, config) {
    const tvOptions = config.TvOptions || {};
    const watchLocations = Array.isArray(tvOptions.WatchLocations) ? tvOptions.WatchLocations : [];
    const leftOverExtensions = Array.isArray(tvOptions.LeftOverFileExtensionsToDelete)
        ? tvOptions.LeftOverFileExtensionsToDelete
        : [];

    view.querySelector('#chkEnableTvSorting').checked = Boolean(tvOptions.IsEnabled);
    view.querySelector('#chkOverwriteExistingEpisodes').checked = Boolean(tvOptions.OverwriteExistingEpisodes);
    view.querySelector('#chkDeleteEmptyFolders').checked = Boolean(tvOptions.DeleteEmptyFolders);

    view.querySelector('#txtMinFileSize').value = tvOptions.MinFileSizeMb ?? 0;
    view.querySelector('#txtSeasonFolderPattern').value = tvOptions.SeasonFolderPattern || '';
    view.querySelector('#txtSeasonZeroName').value = tvOptions.SeasonZeroFolderName || '';
    view.querySelector('#txtWatchFolder').value = watchLocations[0] || '';

    view.querySelector('#txtEpisodePattern').value = tvOptions.EpisodeNamePattern || '';
    view.querySelector('#txtMultiEpisodePattern').value = tvOptions.MultiEpisodeNamePattern || '';
    view.querySelector('#chkPreserveEpisodeFilename').checked = Boolean(tvOptions.PreserveOriginalFilename) || (tvOptions.EpisodeNamePattern === '%fn.%ext' && tvOptions.MultiEpisodeNamePattern === '%fn.%ext');
    view.querySelector('#chkAlwaysCreateSeasonFolders').checked = Boolean(tvOptions.AlwaysCreateSeasonFolders);
    view.querySelector('#chkEnableSeriesAutoDetect').checked = Boolean(tvOptions.AutoDetectSeries);
    view.querySelector('#txtSeriesPattern').value = tvOptions.SeriesFolderPattern || '';
    view.querySelector('#txtDeleteLeftOverFiles').value = leftOverExtensions.join(';');
    view.querySelector('#chkExtendedClean').checked = Boolean(tvOptions.ExtendedClean);
    view.querySelector('#copyOrMoveFile').value = String(Boolean(tvOptions.CopyOriginalFile));
    view.querySelector('#chkQueueLibScan').checked = Boolean(tvOptions.QueueLibraryScan);
}

function onSubmit(view) {
    ApiClient.getNamedConfiguration('autoorganize').then(function (config) {
        const tvOptions = config.TvOptions || {};
        const minFileSize = Number.parseInt(view.querySelector('#txtMinFileSize').value, 10);

        config.TvOptions = tvOptions;
        tvOptions.IsEnabled = view.querySelector('#chkEnableTvSorting').checked;
        tvOptions.OverwriteExistingEpisodes = view.querySelector('#chkOverwriteExistingEpisodes').checked;
        tvOptions.DeleteEmptyFolders = view.querySelector('#chkDeleteEmptyFolders').checked;
        tvOptions.MinFileSizeMb = Number.isNaN(minFileSize) ? 0 : Math.max(0, minFileSize);
        tvOptions.SeasonFolderPattern = view.querySelector('#txtSeasonFolderPattern').value;
        tvOptions.SeasonZeroFolderName = view.querySelector('#txtSeasonZeroName').value;
        tvOptions.EpisodeNamePattern = view.querySelector('#txtEpisodePattern').value;
        tvOptions.MultiEpisodeNamePattern = view.querySelector('#txtMultiEpisodePattern').value;
        tvOptions.PreserveOriginalFilename = view.querySelector('#chkPreserveEpisodeFilename').checked;
        tvOptions.AlwaysCreateSeasonFolders = view.querySelector('#chkAlwaysCreateSeasonFolders').checked;
        tvOptions.AutoDetectSeries = view.querySelector('#chkEnableSeriesAutoDetect').checked;
        tvOptions.DefaultSeriesLibraryPath = view.querySelector('#selectSeriesFolder').value || null;
        tvOptions.SeriesFolderPattern = view.querySelector('#txtSeriesPattern').value;
        tvOptions.LeftOverFileExtensionsToDelete = normalizeExtensions(view.querySelector('#txtDeleteLeftOverFiles').value);
        tvOptions.ExtendedClean = view.querySelector('#chkExtendedClean').checked;

        const watchLocation = view.querySelector('#txtWatchFolder').value.trim();
        tvOptions.WatchLocations = watchLocation ? [watchLocation] : [];
        tvOptions.CopyOriginalFile = view.querySelector('#copyOrMoveFile').value === 'true';
        tvOptions.QueueLibraryScan = view.querySelector('#chkQueueLibScan').checked;

        return ApiClient.updateNamedConfiguration('autoorganize', config);
    }).then(Dashboard.processServerConfigurationUpdateResult, Dashboard.processErrorResponse);

    return false;
}

function onApiFailure(error) {
    Loading.hide();
    Dashboard.alert({
        title: 'Error',
        text: 'Error: ' + getApiErrorMessage(error)
    });
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
    function updateSeriesPatternHelp() {
        const value = getSeriesDirectoryName(view.querySelector('#txtSeriesPattern').value);
        view.querySelector('.seriesPatternDescription').textContent = 'Result: ' + value;
    }

    function updateSeasonPatternHelp() {
        const value = getSeasonDirectoryName(view.querySelector('#txtSeasonFolderPattern').value);
        view.querySelector('.seasonFolderFieldDescription').textContent = 'Result: ' + value;
    }

    function getEffectiveEpisodePattern(patternInput) {
        return view.querySelector('#chkPreserveEpisodeFilename').checked
            ? '%fn.%ext'
            : patternInput.value;
    }

    function updateEpisodePatternHelp() {
        const input = view.querySelector('#txtEpisodePattern');
        const value = getEpisodeFileName(getEffectiveEpisodePattern(input), false);
        view.querySelector('.episodePatternDescription').textContent = 'Result: ' + value;
    }

    function updateMultiEpisodePatternHelp() {
        const input = view.querySelector('#txtMultiEpisodePattern');
        const value = getEpisodeFileName(getEffectiveEpisodePattern(input), true);
        view.querySelector('.multiEpisodePatternDescription').textContent = 'Result: ' + value;
    }

    function toggleEpisodePatterns() {
        const preserveFilename = view.querySelector('#chkPreserveEpisodeFilename').checked;
        view.querySelector('#txtEpisodePattern').disabled = preserveFilename;
        view.querySelector('#txtMultiEpisodePattern').disabled = preserveFilename;
        updateEpisodePatternHelp();
        updateMultiEpisodePatternHelp();
    }

    function selectWatchFolder() {
        const picker = new Dashboard.DirectoryBrowser();

        picker.show({
            callback: function (path) {
                if (path) {
                    view.querySelector('#txtWatchFolder').value = path;
                }

                picker.close();
            },
            header: 'Select Watch Folder',
            validateWriteable: true
        });
    }

    function toggleSeriesLocation() {
        const locationField = view.querySelector('.fldSelectSeriesFolder');
        const locationSelect = view.querySelector('#selectSeriesFolder');

        if (view.querySelector('#chkEnableSeriesAutoDetect').checked) {
            locationField.classList.remove('hide');
            locationSelect.setAttribute('required', 'required');
        } else {
            locationField.classList.add('hide');
            locationSelect.removeAttribute('required');
        }
    }

    function populateSeriesLocation(config) {
        const tvOptions = config.TvOptions || {};

        ApiClient.getVirtualFolders().then(function (result) {
            const mediaLocations = [];

            for (const virtualFolder of result || []) {
                if (virtualFolder.CollectionType !== 'tvshows') {
                    continue;
                }

                for (const location of virtualFolder.Locations || []) {
                    mediaLocations.push({
                        value: location,
                        display: (virtualFolder.Name || 'TV') + ': ' + location
                    });
                }
            }

            const select = view.querySelector('#selectSeriesFolder');
            setSelectOptions(select, mediaLocations, mediaLocations.length > 1);
            select.value = tvOptions.DefaultSeriesLibraryPath || '';
        }, onApiFailure);
    }

    view.querySelector('#txtSeriesPattern').addEventListener('input', updateSeriesPatternHelp);
    view.querySelector('#txtSeasonFolderPattern').addEventListener('input', updateSeasonPatternHelp);
    view.querySelector('#txtEpisodePattern').addEventListener('input', updateEpisodePatternHelp);
    view.querySelector('#txtMultiEpisodePattern').addEventListener('input', updateMultiEpisodePatternHelp);
    view.querySelector('#chkPreserveEpisodeFilename').addEventListener('change', toggleEpisodePatterns);
    view.querySelector('#btnSelectWatchFolder').addEventListener('click', selectWatchFolder);
    view.querySelector('#chkEnableSeriesAutoDetect').addEventListener('change', toggleSeriesLocation);

    view.querySelector('.libraryFileOrganizerForm').addEventListener('submit', function (event) {
        event.preventDefault();
        onSubmit(view);
        return false;
    });

    view.addEventListener('viewshow', function () {
        LibraryMenu.setTabs('autoorganize', 1, getTabs);

        ApiClient.getNamedConfiguration('autoorganize').then(function (config) {
            loadPage(view, config);
            updateSeriesPatternHelp();
            updateSeasonPatternHelp();
            toggleEpisodePatterns();
            populateSeriesLocation(config);
            toggleSeriesLocation();
        }, onApiFailure);
    });
}
