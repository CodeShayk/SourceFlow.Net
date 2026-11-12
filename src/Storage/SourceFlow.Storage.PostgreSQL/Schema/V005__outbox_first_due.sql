-- SourceFlow.Net v3 PostgreSQL schema, version 5: when an outbox message first became due (doc 08 §3.2, §6).
-- A failed publish moves deliver_at to the retry time, which hid a message from the outbox age while the broker was
-- down. first_due_at keeps the original due time from the first retry on, so sourceflow.outbox.age keeps growing
-- through an outage (CH-07) and the delivery-durability SLO fires. A nullable column with no default: no table rewrite.

ALTER TABLE {schema}.outbox ADD COLUMN first_due_at timestamptz NULL;
