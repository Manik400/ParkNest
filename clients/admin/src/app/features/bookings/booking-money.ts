import { BookingDetail } from '../../core/models';

/** One line of the breakdown. `emphasis` marks the line the reader is looking for. */
export interface MoneyLine {
  label: string;
  amount: number;
  note?: string;
  emphasis?: boolean;
}

/**
 * The booking's money, said plainly.
 *
 * `headline` is the one figure to lead with and `headlineLabel` what it is ("Charged", "Fee",
 * "Reserved"); `lines` are the breakdown; `conclusion` is the sentence that says how it ended.
 * `tone` is for colour only — the words carry the meaning.
 */
export interface MoneyStory {
  status: string;
  statusLabel: string;
  headlineLabel: string;
  headline: number;
  lines: MoneyLine[];
  conclusion: string;
  tone: 'good' | 'warn' | 'bad' | 'neutral';
}

/**
 * Turns a booking's figures into something a person can read.
 *
 * The ledger trail is correct and complete and almost nobody can read it: eight rows of debits
 * and credits across four accounts to say "you were charged ninety rupees". This derives the
 * sentence from the same figures, once, so the booking page and the receipt cannot disagree.
 */
export function describeMoney(b: BookingDetail): MoneyStory {
  const s = b.summary;
  const hold = s.holdAmount;
  const settled = s.settledAmount;
  const overstay = b.overstayAmount;
  const fee = b.platformFee;
  const hostGot = Math.max(0, settled + overstay - fee);
  const returned = Math.max(0, hold - settled);

  switch (s.status) {
    case 'Held':
      return {
        status: s.status,
        statusLabel: 'Reserved',
        headlineLabel: 'Reserved from balance',
        headline: hold,
        lines: [
          { label: `${b.bookedMinutes} minutes at ₹${s.ratePerHour}/hr`, amount: hold },
          { label: 'Set aside from the renter’s balance', amount: hold, emphasis: true },
        ],
        conclusion:
          'Nothing has been charged yet. The reserved credits are held until the session ends; unused time comes back.',
        tone: 'neutral',
      };

    case 'Active':
      return {
        status: s.status,
        statusLabel: 'Parked now',
        headlineLabel: 'Reserved from balance',
        headline: hold,
        lines: [
          { label: `${b.bookedMinutes} minutes at ₹${s.ratePerHour}/hr`, amount: hold },
          ...(overstay > 0 ? [{ label: 'Extra time so far', amount: overstay, note: 'billed in 15-minute steps' }] : []),
        ],
        conclusion:
          'The meter is running. Checking out settles the real duration; staying past the slot bills the extra time automatically.',
        tone: 'neutral',
      };

    case 'Cancelled': {
      const late = settled > 0;
      return {
        status: s.status,
        statusLabel: late ? 'Cancelled late' : 'Cancelled',
        headlineLabel: late ? 'Cancellation fee' : 'Charged',
        headline: settled,
        lines: [
          { label: 'Reserved when booked', amount: hold },
          ...(late
            ? [
                { label: 'Late cancellation fee', amount: settled, emphasis: true, note: 'the host could not re-let the slot' },
                { label: 'Of which paid to the host', amount: hostGot },
                { label: 'Of which ParkNest fee', amount: fee },
              ]
            : []),
          { label: 'Returned to the renter’s balance', amount: returned, emphasis: !late },
        ],
        conclusion: late
          ? `Cancelled inside the free window. ₹${settled} was charged as a fee and ₹${returned} went back to the balance.`
          : `Cancelled in time. The whole ₹${hold} went back to the balance; nothing was charged.`,
        tone: late ? 'warn' : 'good',
      };
    }

    case 'Completed':
    case 'Disputed':
    case 'InViolation': {
      const charged = settled + overstay;
      const shortfall = b.shortfallAmount;
      const lines: MoneyLine[] = [
        { label: 'Reserved when booked', amount: hold },
        {
          label: b.billedMinutes != null ? `Parking, ${b.billedMinutes} minutes at ₹${s.ratePerHour}/hr` : 'Parking',
          amount: settled,
        },
        ...(overstay > 0 ? [{ label: 'Extra time past the slot', amount: overstay, note: 'billed in 15-minute steps' }] : []),
        { label: 'Total charged to the renter', amount: charged, emphasis: true },
        ...(returned > 0 ? [{ label: 'Unused time returned to the balance', amount: returned }] : []),
        { label: 'Paid to the host', amount: hostGot },
        { label: 'ParkNest fee', amount: fee },
        ...(shortfall > 0 ? [{ label: 'Still owed by the renter', amount: shortfall, emphasis: true }] : []),
      ];

      if (s.status === 'InViolation') {
        return {
          status: s.status,
          statusLabel: 'Unpaid balance',
          headlineLabel: 'Charged',
          headline: charged,
          lines,
          conclusion: `The session ended with ₹${shortfall} the renter could not cover. Their access is restricted until it is settled.`,
          tone: 'bad',
        };
      }

      if (s.status === 'Disputed') {
        return {
          status: s.status,
          statusLabel: 'Under dispute',
          headlineLabel: 'Charged',
          headline: charged,
          lines,
          conclusion: `₹${charged} was charged and is being reviewed. Any refund is posted as a new transaction; this one is not rewritten.`,
          tone: 'warn',
        };
      }

      return {
        status: s.status,
        statusLabel: 'Paid',
        headlineLabel: 'Charged',
        headline: charged,
        lines,
        conclusion:
          returned > 0
            ? `Paid in full. ₹${charged} was charged and ₹${returned} of unused time went back to the balance.`
            : `Paid in full. ₹${charged} was charged.`,
        tone: 'good',
      };
    }

    default:
      return {
        status: s.status,
        statusLabel: s.status,
        headlineLabel: 'Amount',
        headline: settled || hold,
        lines: [],
        conclusion: '',
        tone: 'neutral',
      };
  }
}
