from __future__ import annotations

import argparse
from collections.abc import Sequence


def _rename_mapping(value: str) -> tuple[str, str]:
    old_name, separator, new_name = value.partition("=")
    if not separator or not old_name or not new_name:
        raise argparse.ArgumentTypeError("expected OLD=NEW")
    return old_name, new_name


def _build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        prog="unity-vecenv",
        description="Utilities for UnityVecEnv.",
    )
    subcommands = parser.add_subparsers(dest="command", required=True)

    onnx_rename = subcommands.add_parser(
        "onnx-rename",
        help="Rename tensors in an ONNX model.",
    )
    onnx_rename.add_argument("input", help="Input ONNX model.")
    onnx_rename.add_argument("output", help="Output ONNX model.")
    onnx_rename.add_argument(
        "--unity-defaults",
        action="store_true",
        help="Rename the conventional policy inputs and outputs for Unity.",
    )
    onnx_rename.add_argument(
        "--rename",
        action="append",
        default=[],
        metavar="OLD=NEW",
        type=_rename_mapping,
        help="Additional tensor rename. May be supplied more than once.",
    )
    onnx_rename.add_argument(
        "--no-validate",
        action="store_true",
        help="Skip ONNX model validation before saving.",
    )
    onnx_rename.set_defaults(handler=_run_onnx_rename)
    return parser


def _run_onnx_rename(args: argparse.Namespace, parser: argparse.ArgumentParser) -> int:
    if not args.unity_defaults and not args.rename:
        parser.error("provide --unity-defaults or at least one --rename OLD=NEW")

    try:
        from unity_vecenv.onnx_utilities.onnx_rename import (
            DEFAULT_UNITY_RENAMES,
            rename_onnx_tensors,
        )
    except ModuleNotFoundError as exc:
        if exc.name == "onnx":
            parser.error(
                "the ONNX utilities are not installed; "
                'install them with `pip install "unity-vecenv[onnx]"`'
            )
        raise

    renames: dict[str, str] = {}
    if args.unity_defaults:
        renames.update(DEFAULT_UNITY_RENAMES)
    renames.update(args.rename)

    rename_onnx_tensors(
        args.input,
        args.output,
        renames,
        validate=not args.no_validate,
    )
    return 0


def main(argv: Sequence[str] | None = None) -> int:
    parser = _build_parser()
    args = parser.parse_args(argv)
    return args.handler(args, parser)
