"""One exclusive diagnostic attachment per receipt-owned native process."""
import atexit
import msvcrt


def acquire_native_lease(launch):
    path = launch.parent / 'native-control.lock'
    if path.is_symlink() or (path.exists() and path.is_junction()):
        raise ValueError('Diagnostic lease cannot be a filesystem link')
    lease = path.open('a+b')
    lease.seek(0)
    try:
        msvcrt.locking(lease.fileno(), msvcrt.LK_NBLCK, 1)
    except OSError:
        lease.close()
        raise RuntimeError('Another native diagnostic is attached to this owned launch') from None
    atexit.register(lease.close)
    return lease
