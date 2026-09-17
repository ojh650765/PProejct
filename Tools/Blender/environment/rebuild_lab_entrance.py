"""Compatibility entry point. Keep the complete building kit and doorway table in sync."""
from pathlib import Path
path = Path(__file__).with_name("rebuild_buildings.py")
exec(compile(path.read_text(encoding="utf-8"), str(path), "exec"), {"__file__": str(path)})
