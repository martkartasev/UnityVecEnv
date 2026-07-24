import pytest
from unity_vecenv.cli import main


def test_cli_help(capsys):
    with pytest.raises(SystemExit, match="0"):
        main(["--help"])

    assert "onnx-rename" in capsys.readouterr().out


def test_onnx_rename_requires_a_mapping(capsys):
    with pytest.raises(SystemExit, match="2"):
        main(["onnx-rename", "input.onnx", "output.onnx"])

    assert "provide --unity-defaults" in capsys.readouterr().err
