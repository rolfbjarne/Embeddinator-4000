#include <stdio.h>
#include <stdlib.h>
#include <unistd.h>
#include <string.h>
#include <errno.h>
#include <time.h>

static void __perform_leak_check () __attribute__ ((destructor));

static void
__perform_leak_check ()
{
	fprintf (stderr, "linked in leak check skipped\n");
	return;

	fprintf (stderr, "Checking for leak checks\n");
	const char *ready_file = getenv ("LEAK_READY_FILE");
	if (!ready_file)
		return;
	const char *done_file = getenv ("LEAK_DONE_FILE");
	if (!done_file)
		return;

	int rv = unlink (ready_file);
	if (rv != 0) {
		fprintf (stderr, "Could not delete ready file %s: %s\n", ready_file, strerror (errno));;
		return;
	}

	rv = access (done_file, F_OK);
	while (rv == 0) {
		fprintf (stdout, "Waiting for done file to be deleted...\n");
		struct timespec ts;
		ts.tv_sec = 0;
		ts.tv_nsec = 100000000; /* 100 ms */
		nanosleep (&ts, NULL);
		rv = access (done_file, F_OK);
	}
	if (errno != ENOENT)
		fprintf (stdout, "Failed to access done file %s: %s\n", done_file, strerror (errno));
	fprintf (stdout, "Leak check performed\n");
}
