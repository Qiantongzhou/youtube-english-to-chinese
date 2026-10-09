"""A harmless long-lived child for the desktop cancellation test."""
from pathlib import Path
import subprocess
import sys
import time

child = subprocess.Popen([sys.executable, "-c", "import time; time.sleep(120)"])
path = Path(sys.argv[1])
path.with_suffix(".tmp").write_text(str(child.pid), encoding="ascii")
path.with_suffix(".tmp").replace(path)
print("fixture ready", flush=True)
time.sleep(120)
