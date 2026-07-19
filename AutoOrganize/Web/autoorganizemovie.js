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

function getMovieNamePreview(value) {
    const movieName = 'Movie Name';
    const movieYear = '2017';
    const fileNameWithoutExtension = movieName + '.' + movieYear + '.MULTI.1080p.BluRay.DTS.x264-UTT';

    return applyReplacements(value, [
        ['%m.n', replaceAll(movieName, ' ', '.')],
        ['%m_n', replaceAll(movieName, ' ', '_')],
        ['%mn', movieName],
        ['%my', movieYear],
        ['%ext', 'mkv'],
        ['%fn', fileNameWithoutExtension]
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

function parseWatchLocations(value) {
    return String(value ?? '').split(/\r?\n/).map(path => path.trim()).filter(Boolean);
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
    const movieOptions = config.MovieOptions || {};
    const watchLocations = Array.isArray(movieOptions.WatchLocations) ? movieOptions.WatchLocations : [];
    const leftOverExtensions = Array.isArray(movieOptions.LeftOverFileExtensionsToDelete)
        ? movieOptions.LeftOverFileExtensionsToDelete
        : [];

    view.querySelector('#chkEnableMovieSorting').checked = Boolean(movieOptions.IsEnabled);
    view.querySelector('#chkOverwriteExistingMovies').checked = Boolean(movieOptions.OverwriteExistingFiles);
    view.querySelector('#chkDeleteEmptyMovieFolders').checked = Boolean(movieOptions.DeleteEmptyFolders);
    view.querySelector('#txtMovieMinFileSize').value = movieOptions.MinFileSizeMb ?? 0;
    view.querySelector('#txtMoviePattern').value = movieOptions.MoviePattern || '';
    view.querySelector('#chkPreserveMovieFilename').checked = Boolean(movieOptions.PreserveOriginalFilename) || movieOptions.MoviePattern === '%fn.%ext';
    view.querySelector('#txtWatchMovieFolder').value = watchLocations.join('\n');
    view.querySelector('#chkSubMovieFolders').checked = Boolean(movieOptions.MovieFolder);
    view.querySelector('#txtMovieFolderPattern').value = movieOptions.MovieFolderPattern || '';
    view.querySelector('#txtDeleteLeftOverMovieFiles').value = leftOverExtensions.join(';');
    view.querySelector('#chkExtendedClean').checked = Boolean(movieOptions.ExtendedClean);
    view.querySelector('#chkEnableMovieAutoDetect').checked = Boolean(movieOptions.AutoDetectMovie);
    view.querySelector('#copyOrMoveMovieFile').value = String(Boolean(movieOptions.CopyOriginalFile));
    view.querySelector('#chkQueueLibScan').checked = Boolean(movieOptions.QueueLibraryScan);
    view.querySelector('#chkRequireMovieApproval').checked = Boolean(movieOptions.RequireApproval);
}

async function onSubmit(view) {
    const button = view.querySelector('.libraryFileOrganizerForm button[type="submit"]');
    const label = button.querySelector('.aoSaveLabel');

    if (button.disabled) {
        return false;
    }

    button.disabled = true;
    button.setAttribute('aria-busy', 'true');
    label.textContent = 'Saving…';

    try {
        const config = await ApiClient.getNamedConfiguration('autoorganize');
        const movieOptions = config.MovieOptions || {};
        const minFileSize = Number.parseInt(view.querySelector('#txtMovieMinFileSize').value, 10);

        config.MovieOptions = movieOptions;
        movieOptions.IsEnabled = view.querySelector('#chkEnableMovieSorting').checked;
        movieOptions.OverwriteExistingFiles = view.querySelector('#chkOverwriteExistingMovies').checked;
        movieOptions.DeleteEmptyFolders = view.querySelector('#chkDeleteEmptyMovieFolders').checked;
        movieOptions.MinFileSizeMb = Number.isNaN(minFileSize) ? 0 : Math.max(0, minFileSize);
        movieOptions.MoviePattern = view.querySelector('#txtMoviePattern').value;
        movieOptions.PreserveOriginalFilename = view.querySelector('#chkPreserveMovieFilename').checked;
        movieOptions.LeftOverFileExtensionsToDelete = normalizeExtensions(view.querySelector('#txtDeleteLeftOverMovieFiles').value);
        movieOptions.ExtendedClean = view.querySelector('#chkExtendedClean').checked;
        movieOptions.AutoDetectMovie = view.querySelector('#chkEnableMovieAutoDetect').checked;
        movieOptions.DefaultMovieLibraryPath = view.querySelector('#selectMovieFolder').value || null;
        movieOptions.MovieFolder = view.querySelector('#chkSubMovieFolders').checked;
        movieOptions.MovieFolderPattern = view.querySelector('#txtMovieFolderPattern').value;

        movieOptions.WatchLocations = parseWatchLocations(view.querySelector('#txtWatchMovieFolder').value);
        movieOptions.CopyOriginalFile = view.querySelector('#copyOrMoveMovieFile').value === 'true';
        movieOptions.QueueLibraryScan = view.querySelector('#chkQueueLibScan').checked;
        movieOptions.RequireApproval = view.querySelector('#chkRequireMovieApproval').checked;

        const result = await ApiClient.updateNamedConfiguration('autoorganize', config);
        Dashboard.processServerConfigurationUpdateResult(result);
    } catch (error) {
        Dashboard.processErrorResponse(error);
    } finally {
        button.disabled = false;
        button.setAttribute('aria-busy', 'false');
        label.textContent = 'Save changes';
    }

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
    function updateMoviePatternHelp() {
        const pattern = view.querySelector('#chkPreserveMovieFilename').checked
            ? '%fn.%ext'
            : view.querySelector('#txtMoviePattern').value;
        const value = getMovieNamePreview(pattern);
        view.querySelector('.moviePatternDescription').textContent = 'Result: ' + value;
    }

    function toggleMoviePattern() {
        view.querySelector('#txtMoviePattern').disabled =
            view.querySelector('#chkPreserveMovieFilename').checked;
        updateMoviePatternHelp();
    }

    function updateMovieFolderPatternHelp() {
        const value = getMovieNamePreview(view.querySelector('#txtMovieFolderPattern').value);
        view.querySelector('.movieFolderPatternDescription').textContent = 'Result: ' + value;
    }

    function toggleMovieFolderPattern() {
        view.querySelector('.fldSelectMovieFolderPattern').classList.toggle(
            'hide',
            !view.querySelector('#chkSubMovieFolders').checked);
    }

    function selectWatchFolder() {
        const picker = new Dashboard.DirectoryBrowser();

        picker.show({
            callback: function (path) {
                if (path) {
                    const field = view.querySelector('#txtWatchMovieFolder');
                    field.value = [...new Set([...parseWatchLocations(field.value), path])].join('\n');
                }

                picker.close();
            },
            header: 'Select Watch Folder',
            validateWriteable: true
        });
    }

    function toggleMovieLocation() {
        const locationField = view.querySelector('.fldSelectMovieFolder');
        const locationSelect = view.querySelector('#selectMovieFolder');

        if (view.querySelector('#chkEnableMovieAutoDetect').checked) {
            locationField.classList.remove('hide');
            locationSelect.setAttribute('required', 'required');
        } else {
            locationField.classList.add('hide');
            locationSelect.removeAttribute('required');
        }
    }

    function updateOrganizerState() {
        const enabled = view.querySelector('#chkEnableMovieSorting').checked;
        const form = view.querySelector('.libraryFileOrganizerForm');

        form.querySelectorAll('input, select, button').forEach(function (control) {
            if (control.id !== 'chkEnableMovieSorting' && control.type !== 'submit') {
                control.disabled = !enabled;
            }
        });
        view.querySelectorAll('.aoDependent').forEach(function (section) {
            section.classList.toggle('aoDependentDisabled', !enabled);
        });

        if (enabled) {
            toggleMoviePattern();
            toggleMovieLocation();
        }
    }

    function populateMovieLocation(config) {
        const movieOptions = config.MovieOptions || {};

        ApiClient.getVirtualFolders().then(function (result) {
            const mediaLocations = [];

            for (const virtualFolder of result || []) {
                if (virtualFolder.CollectionType !== 'movies') {
                    continue;
                }

                for (const location of virtualFolder.Locations || []) {
                    mediaLocations.push({
                        value: location,
                        display: (virtualFolder.Name || 'Movies') + ': ' + location
                    });
                }
            }

            const select = view.querySelector('#selectMovieFolder');
            setSelectOptions(select, mediaLocations, mediaLocations.length > 1);
            select.value = movieOptions.DefaultMovieLibraryPath || (mediaLocations.length === 1 ? mediaLocations[0].value : '');
        }, onApiFailure);
    }

    view.querySelector('#btnSelectWatchMovieFolder').addEventListener('click', selectWatchFolder);
    view.querySelector('#txtMoviePattern').addEventListener('input', updateMoviePatternHelp);
    view.querySelector('#chkPreserveMovieFilename').addEventListener('change', toggleMoviePattern);
    view.querySelector('#chkSubMovieFolders').addEventListener('change', toggleMovieFolderPattern);
    view.querySelector('#txtMovieFolderPattern').addEventListener('input', updateMovieFolderPatternHelp);
    view.querySelector('#chkEnableMovieAutoDetect').addEventListener('change', toggleMovieLocation);
    view.querySelector('#chkEnableMovieSorting').addEventListener('change', updateOrganizerState);

    view.querySelector('.libraryFileOrganizerForm').addEventListener('submit', function (event) {
        event.preventDefault();
        onSubmit(view);
        return false;
    });

    view.addEventListener('viewshow', function () {
        LibraryMenu.setTabs('autoorganize', 2, getTabs);

        ApiClient.getNamedConfiguration('autoorganize').then(function (config) {
            loadPage(view, config);
            toggleMoviePattern();
            updateMovieFolderPatternHelp();
            populateMovieLocation(config);
            toggleMovieLocation();
            toggleMovieFolderPattern();
            updateOrganizerState();
        }, onApiFailure);
    });
}
