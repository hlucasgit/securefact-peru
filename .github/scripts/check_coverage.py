"""Fails the build when the merged code coverage is below the floor.

Usage: check_coverage.py <Summary.json of ReportGenerator> <minimum line coverage %> <minimum branch coverage %>
The floors are a ratchet, not a goal: raise them when the coverage rises, never lower them to let a change through.
"""
import json
import sys


def main() -> int:
    path, min_line, min_branch = sys.argv[1], float(sys.argv[2]), float(sys.argv[3])
    with open(path, encoding="utf-8") as handle:
        summary = json.load(handle)["summary"]
    line, branch = float(summary["linecoverage"]), float(summary["branchcoverage"])
    print(f"line coverage {line:.1f}% (floor {min_line:.1f}%), branch coverage {branch:.1f}% (floor {min_branch:.1f}%)")
    failed = False
    if line < min_line:
        print(f"::error::Line coverage {line:.1f}% is below the floor of {min_line:.1f}%.")
        failed = True
    if branch < min_branch:
        print(f"::error::Branch coverage {branch:.1f}% is below the floor of {min_branch:.1f}%.")
        failed = True
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
