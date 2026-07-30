"""Pinned WeasyPrint PDF/X profiles required by the current vendor target."""

from functools import partial

from weasyprint.pdf import VARIANTS
from weasyprint.pdf.pdfx import pdfx


PROFILE_NAME = "pdf/x-1a:2001"
DECLARED_STANDARD = "PDF/X-1a:2001"


def register_pdfx_2001_profile() -> None:
    """Register the older PDF/X-1a profile absent from WeasyPrint 69."""
    VARIANTS[PROFILE_NAME] = (
        partial(pdfx, version=1, variant="a:2001"),
        {
            "pdf_version": "1.3",
            "pdf_identifier": True,
            "output_intent": "--lorekeeper-press",
        },
    )
