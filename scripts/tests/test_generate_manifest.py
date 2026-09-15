import hashlib
import importlib.util
import io
from pathlib import Path
import unittest
import zipfile


SCRIPT_PATH = Path(__file__).parents[1] / "generate_manifest.py"


def load_generator():
    if not SCRIPT_PATH.exists():
        raise AssertionError("The manifest generator script is missing.")

    spec = importlib.util.spec_from_file_location("generate_manifest", SCRIPT_PATH)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def plugin_zip(*extra_files, include_models=True):
    output = io.BytesIO()
    with zipfile.ZipFile(output, "w", zipfile.ZIP_DEFLATED) as archive:
        archive.writestr("AutoOrganize.dll", b"plugin-binary")
        archive.writestr("Lingua.dll", b"lingua-binary")
        if include_models:
            archive.writestr("Lingua/LanguageModels/en/model.json", b"language-model")
        for name in extra_files:
            archive.writestr(name, b"unexpected")
    return output.getvalue()


def root_metadata():
    return """\
name: "Auto Organize"
guid: "70b7b43b-471b-4159-b4be-56750c795499"
owner: "community-maintained"
overview: "Automatically organize new media"
description: "Automatically organize TV episodes and movies for Jellyfin 12"
category: "General"
"""


class ManifestGeneratorTests(unittest.TestCase):
    def test_publishes_empty_version_list_after_last_zip_release_is_deleted(self):
        generator = load_generator()
        manifest = generator.build_manifest(
            "owner/plugin",
            root_metadata(),
            [],
            lambda _: self.fail("No asset should be downloaded"),
            lambda _: self.fail("No tag metadata should be downloaded"),
        )
        self.assertEqual([], manifest[0]["versions"])

    def test_lists_installable_zip_releases_newest_first(self):
        generator = load_generator()
        first_zip = plugin_zip()
        second_zip = plugin_zip()
        releases = [
            {
                "tag_name": "v1",
                "draft": False,
                "published_at": "2026-01-01T00:00:00Z",
                "body": "First release\n\n**Full Changelog**: ignored",
                "assets": [
                    {
                        "name": "AutoOrganize_1.0.0.0.zip",
                        "browser_download_url": "https://example.test/AutoOrganize_1.0.0.0.zip",
                    }
                ],
            },
            {
                "tag_name": "archive-only",
                "draft": False,
                "published_at": "2025-12-01T00:00:00Z",
                "body": "Not installable",
                "assets": [
                    {
                        "name": "AutoOrganize.7z",
                        "browser_download_url": "https://example.test/AutoOrganize.7z",
                    }
                ],
            },
            {
                "tag_name": "v2",
                "draft": False,
                "published_at": "2026-02-01T00:00:00Z",
                "body": "Second release",
                "assets": [
                    {
                        "name": "AutoOrganize_2.0.0.0.zip",
                        "browser_download_url": "https://example.test/AutoOrganize_2.0.0.0.zip",
                    }
                ],
            },
        ]
        downloads = {
            "https://example.test/AutoOrganize_1.0.0.0.zip": first_zip,
            "https://example.test/AutoOrganize_2.0.0.0.zip": second_zip,
        }
        tagged_metadata = {
            "v1": 'version: 1\ntargetAbi: "10.11.0.0"\n',
            "v2": 'version: "2.0.0.0"\ntargetAbi: "12.0.0.0"\n',
        }

        manifest = generator.build_manifest(
            "owner/plugin",
            root_metadata(),
            releases,
            downloads.__getitem__,
            tagged_metadata.__getitem__,
        )

        self.assertEqual("Auto Organize", manifest[0]["name"])
        self.assertEqual(
            ["2.0.0.0", "1.0.0.0"],
            [version["version"] for version in manifest[0]["versions"]],
        )
        self.assertEqual("12.0.0.0", manifest[0]["versions"][0]["targetAbi"])
        self.assertEqual("Second release", manifest[0]["versions"][0]["changelog"])
        self.assertEqual(hashlib.md5(first_zip).hexdigest(), manifest[0]["versions"][1]["checksum"])

    def test_rejects_zip_without_language_models(self):
        generator = load_generator()
        release = self.release("https://example.test/plugin.zip")

        with self.assertRaisesRegex(ValueError, "missing Lingua language models"):
            generator.build_manifest(
                "owner/plugin",
                root_metadata(),
                [release],
                lambda _: plugin_zip(include_models=False),
                lambda _: 'version: "1.0.0.0"\ntargetAbi: "12.0.0.0"\n',
            )

    def test_rejects_zip_with_unexpected_payload(self):
        generator = load_generator()
        release = self.release("https://example.test/plugin.zip")

        with self.assertRaisesRegex(ValueError, "contains unexpected files"):
            generator.build_manifest(
                "owner/plugin",
                root_metadata(),
                [release],
                lambda _: plugin_zip("extra.txt"),
                lambda _: 'version: "1.0.0.0"\ntargetAbi: "12.0.0.0"\n',
            )

    @staticmethod
    def release(url):
        return {
            "tag_name": "v1",
            "draft": False,
            "published_at": "2026-01-01T00:00:00Z",
            "body": "Release",
            "assets": [
                {
                    "name": "AutoOrganize_1.0.0.0.zip",
                    "browser_download_url": url,
                }
            ],
        }


if __name__ == "__main__":
    unittest.main()
