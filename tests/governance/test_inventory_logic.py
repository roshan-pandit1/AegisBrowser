"""Pure in-memory checks for the inventory checker's refusal decisions.

Source only: do not run until this file, its imports and interpreter have been
reviewed for the approved Profile A environment and write boundary.
"""

import unittest
from pathlib import PurePosixPath

from check_inventory import (
    REQUIRED_FIELDS,
    canonical_relative,
    compare_inventory,
    comparison_is_clean,
    object_without_duplicates,
    operation_is_declared,
    skip_generated_directory,
)


def entry(category="SAFE_READ_ONLY", digest="A" * 64):
    result = {field: "reviewed" for field in REQUIRED_FIELDS}
    result.update({"category": category, "observed_sha256": digest})
    return result


class InventoryLogicTests(unittest.TestCase):
    def test_only_exact_inspection_declaration_is_accepted(self):
        self.assertFalse(operation_is_declared([]))
        self.assertFalse(operation_is_declared(["--profile=B:inspect-inventory"]))
        self.assertFalse(operation_is_declared(["--profile=A:inspect-inventory", "extra"]))
        self.assertTrue(operation_is_declared(["--profile=A:inspect-inventory"]))

    def test_known_matching_entry_is_clean(self):
        result = compare_inventory({"item": ("item", entry())}, {"item": "A" * 64})
        self.assertTrue(comparison_is_clean(result))

    def test_unknown_category_is_refused(self):
        result = compare_inventory({"item": ("item", entry("UNKNOWN"))}, {"item": "A" * 64})
        self.assertEqual(result["unknown"], 1)
        self.assertFalse(comparison_is_clean(result))

    def test_unknown_operation_field_is_refused(self):
        item = entry()
        item["network_operations"] = "UNKNOWN"
        result = compare_inventory({"item": ("item", item)}, {"item": "A" * 64})
        self.assertEqual(result["unknown"], 1)
        self.assertFalse(comparison_is_clean(result))

    def test_blank_missing_and_invalid_required_fields_are_refused(self):
        for value in ("", " \t\r\n ", None, 7):
            item = entry()
            item["network_operations"] = value
            result = compare_inventory({"item": ("item", item)}, {"item": "A" * 64})
            self.assertEqual(result["unknown"], 1)
            self.assertFalse(comparison_is_clean(result))
        item = entry()
        del item["network_operations"]
        result = compare_inventory({"item": ("item", item)}, {"item": "A" * 64})
        self.assertEqual(result["unknown"], 1)
        self.assertFalse(comparison_is_clean(result))

    def test_only_named_workspace_artifacts_are_excluded(self):
        root = PurePosixPath("/workspace")
        for relative in (
            "artifacts/recovery-backup-inventory-governance",
            "artifacts/governance-inventory-run",
            "artifacts/governance-decision-run-final",
        ):
            self.assertTrue(skip_generated_directory(root / relative, root))
        for relative in (
            "artifacts",
            "artifacts/governance-inventory-run-extra",
            "artifacts/recovery-backup-inventory-governance-extra",
            "artifacts/legitimate-source",
        ):
            self.assertFalse(skip_generated_directory(root / relative, root))
        with self.assertRaises(ValueError):
            skip_generated_directory(root / "artifacts/unknown/bin", root)

    def test_new_and_missing_entries_are_refused(self):
        self.assertFalse(comparison_is_clean(compare_inventory({}, {"new": "A" * 64})))
        self.assertFalse(comparison_is_clean(compare_inventory({"old": ("old", entry())}, {})))

    def test_changed_or_missing_digest_is_refused(self):
        self.assertFalse(comparison_is_clean(
            compare_inventory({"item": ("item", entry())}, {"item": "B" * 64})
        ))
        self.assertFalse(comparison_is_clean(
            compare_inventory({"item": ("item", entry(digest=None))}, {"item": "A" * 64})
        ))

    def test_unsafe_inventory_paths_are_refused(self):
        for value in ("../outside", "/absolute", "C:relative", "folder/../item", "a\\b", "café"):
            with self.subTest(kind="unsafe_path"):
                with self.assertRaises(ValueError):
                    canonical_relative(value)

    def test_duplicate_json_keys_are_refused(self):
        with self.assertRaises(ValueError):
            object_without_duplicates([("key", 1), ("key", 2)])


if __name__ == "__main__":
    unittest.main()
