"""OS desktop prompts for an owner's one-time iPhone unlock approval.

The browser banner is always available. This prompt is an additional way for
the owner to answer while another window is in front. No passcode is passed to
the notification program.
"""
import sys


def command(port, platform=None):
    platform = platform or sys.platform
    message = ("An agent asks to enter the saved iPhone passcode. "
               f"Approve here or at http://localhost:{int(port)}")
    if platform == "win32":
        script = (
            "Add-Type -AssemblyName System.Windows.Forms; "
            "[System.Windows.Forms.MessageBox]::Show("
            f"'{message}', 'Unlock the iPhone?', "
            "[System.Windows.Forms.MessageBoxButtons]::YesNo, "
            "[System.Windows.Forms.MessageBoxIcon]::Question, "
            "[System.Windows.Forms.MessageBoxDefaultButton]::Button2)"
        )
        return ["powershell.exe", "-NoProfile", "-Command", script]
    if platform == "darwin":
        script = (f'display dialog "{message}" buttons {{"Deny", "Unlock"}} '
                  'default button "Deny" with title "Unlock the iPhone?"')
        return ["osascript", "-e", script]
    return ["notify-send", "-a", "phoneuse", "-u", "critical", "--wait",
            "-A", "unlock=Unlock", "-A", "deny=Deny", "Unlock the iPhone?", message]


def approved(output):
    # Windows MessageBox prints Yes/No; osascript prints "button returned:...";
    # notify-send prints the action id.
    return output.strip().lower() in ("yes", "button returned:unlock", "unlock")


def answered(output):
    return output.strip().lower() in ("yes", "no", "button returned:unlock",
                                       "button returned:deny", "unlock", "deny")
