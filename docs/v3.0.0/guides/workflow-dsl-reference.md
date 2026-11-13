# Workflow DSL reference (`sourceflow.workflow: 1`)

<!-- Generated from schemas/workflow-1.json by `npm run docs:dsl` in studio/ (studio/scripts/dsl-reference.mjs). Edit the schema, not this file. -->

The workflow DSL of SourceFlow Studio (design doc 15 §5). Structure only: references to message types, owners, routes and schemas are checked by the validator (WF-V01..V16). Workflow files are YAML without anchors or aliases.

This reference lists every key the schema allows. The [workflows guide](workflows.md) explains how to use them, and design doc 15 §5.3 and §5.4 give the execution semantics and the validation rules.

## The file

<a id="root"></a>

| Key | Type | Required | Description |
|-----|------|----------|-------------|
| `sourceflow.workflow` | `1` | yes | The DSL version. |
| `name` | [kebab](#kebab) | yes | The workflow name; with the version it is the process name name@version. |
| `version` | integer | yes | Structural changes to a published workflow need a new version (WF-V15). |
| `owner` | string matching `^[a-z][a-z0-9]*([.-][a-z0-9]+)*$` | yes | The orchestrating service; the workflow is generated into it. |
| `description` | string |  | What the workflow is for, in a sentence. Shown in Studio; it does not change the fingerprint. |
| `start` | [start](#start) | yes | The event that starts an instance, and how later messages find it. |
| `data` | map of [identifier](#identifier) to [dataField](#datafield) |  | Instance data. Captured fields are added from their event. |
| `steps` | list of [step](#step) | yes | The steps, run in order unless a step says next or a failure says goto. |
| `on` | [on](#on) |  | What happens when an instance ends: completed, compensated, or stopped for an operator. |

## `start`

<a id="start"></a>

The event that starts an instance, and how later messages find it.

| Key | Type | Required | Description |
|-----|------|----------|-------------|
| `on` | [messageType](#messagetype) | yes | The start event: a domain event of the owner, or an integration event the owner subscribes to. |
| `correlate` | string matching `^data(\.[A-Za-z_][A-Za-z0-9_]*)+$` | yes | The path in the start event of the instance id, for example data.orderId. Awaited events that carry the same field correlate by it (WF-V04). |
| `when` | [guard](#guard) |  | Starts an instance only when this guard over event.data.* holds. |

## `data` fields

<a id="datafield"></a>

One field of the instance data: copied from the start event (from), or typed with an optional default.

| Key | Type | Required | Description |
|-----|------|----------|-------------|
| `from` | string matching `^start\.data(\.[A-Za-z_][A-Za-z0-9_]*)+$` |  | Copies the value from the start event, for example start.data.total. |
| `type` | `string` or `integer` or `number` or `boolean` or `uuid` or `date-time` or `date` or `time` or `duration` or `uri` or `array` or `object` |  | The field type when it is not copied from the start event. |
| `default` | string or number or boolean or null |  | The initial value of a typed field. |
| `personal` | boolean |  | Personal data: crypto-shredded in the workflow state (WF-V14). |

Give exactly one of `from` or `type`.

## Step

<a id="step"></a>

A step: send a command (or nothing), wait for its outcome, and say what happens on failure, on timeout and when undoing.

| Key | Type | Required | Description |
|-----|------|----------|-------------|
| `id` | [kebab](#kebab) | yes | The step id: lower-case words joined by hyphens, unique in the workflow; complete is reserved. |
| `send` | [send](#send) |  | The command the step sends to its owner. Without it, the step only waits. |
| `await` | [await](#await) | yes | What the step waits for: the success event, and the failures it handles. |
| `timeout` | [timeout](#timeout) |  | How long the step waits, and what happens then. Every wait needs one, or none with a reason (WF-V06). |
| `compensate` | [compensate](#compensate) |  | How to undo the step after a later failure: compensations run in reverse order (WF-V09). |
| `pivot` | boolean |  | The point of no return: later failures recover forward (WF-V07). |
| `when` | [guard](#guard) |  | A guard over $data.*: when it doesn't hold, the step is skipped. |
| `next` | [kebab](#kebab) |  | The step after a success, or complete. |

## `send`

<a id="send"></a>

A command and the values of its fields.

| Key | Type | Required | Description |
|-----|------|----------|-------------|
| `command` | [messageType](#messagetype) | yes | The command, owned by exactly one service (WF-V02). |
| `map` | map of [identifier](#identifier) to [mapValue](#mapvalue) |  | The command's fields, each a $data path, an event.data path (start event, first step), $failure.code, a literal, or a $hook (WF-V12). |

## `map` values

<a id="mapvalue"></a>

$data.x, event.data.x, $failure.code, a literal, or { $hook: Name }.

One of:

- string matching `^(\$data\|event\.data\|\$failure)(\.[A-Za-z_][A-Za-z0-9_]*)+$`: A path: $data.<field>, event.data.<field> (the start event, in the first step only) or $failure.code / $failure.message (in on.compensated).
- string: A literal string.
- number or boolean or null: A literal number, boolean or null (null only into a nullable field).
- an object with `$hook`: A value computed in C#: a static partial method of that name on the generated workflow record (SF0021 when it is missing).

  | Key | Type | Required | Description |
  |-----|------|----------|-------------|
  | `$hook` | string matching `^[A-Z][A-Za-z0-9]*$` | yes | The method name, in PascalCase. |

## `await`

<a id="await"></a>

The success event and the failures a step handles.

| Key | Type | Required | Description |
|-----|------|----------|-------------|
| `success` | [success](#success) | yes | The event that completes the step (WF-V03). |
| `failure` | list of [failure](#failure) |  | The rejections and failure events the step handles; anything else stops the instance for an operator. |

## `success`

<a id="success"></a>

The success event, as a message type, or with captures and a guard.

One of:

- [messageType](#messagetype)
- an object with `on`, `capture`, `when`

  | Key | Type | Required | Description |
  |-----|------|----------|-------------|
  | `on` | [messageType](#messagetype) | yes | The success event. |
  | `capture` | map of [identifier](#identifier) to string matching `^event\.data(\.[A-Za-z_][A-Za-z0-9_]*)+$` |  | Instance data to take from the event, for example paymentId: event.data.paymentId. |
  | `when` | [guard](#guard) |  | The event completes the step only when this guard over $data.* and event.data.* holds. |

## `failure`

<a id="failure"></a>

One failure a step handles: a rejection of its command, or a failure event.

| Key | Type | Required | Description |
|-----|------|----------|-------------|
| `rejected` | [code](#code) |  | A rejection code of the command (WF-V08: in its rejects, with replyOnReject on a remote route). |
| `on` | [messageType](#messagetype) |  | A failure event. |
| `retry` | [retry](#retry) |  | Sends the command again before giving up (business retry). |
| `then` | [then](#then) |  | What happens when the retries are used up. |

Give exactly one of `rejected` or `on`.

## `timeout`

<a id="timeout"></a>

A wait's timeout: after a duration, retried, then what happens; or none with a reason.

One of:

- `none`: No timeout; WF-V06 asks for the object form with a reason.
- an object with `after`, `none`, `reason`, `retry`, `then`

  | Key | Type | Required | Description |
  |-----|------|----------|-------------|
  | `after` | [duration](#duration) |  | How long each attempt waits. |
  | `none` | boolean |  | No timeout (the step can wait for ever); give the reason. |
  | `reason` | string |  | Why the wait has no timeout. |
  | `retry` | [retry](#retry) |  | Sends the command again when an attempt times out. |
  | `then` | [then](#then) |  | What happens when the last attempt times out (default fail). |

## `retry`

<a id="retry"></a>

Bounded re-attempts; WF-V10 checks 1 <= max <= 20 and the delays.

| Key | Type | Required | Description |
|-----|------|----------|-------------|
| `max` | integer | yes | How many more attempts, 1 to 20. |
| `backoff` | `fixed` or `exponential` |  | The delay between attempts: always initial (fixed), or doubling from initial (exponential). Without initial, attempts follow at once. |
| `initial` | [duration](#duration) |  | The first delay. |
| `cap` | [duration](#duration) |  | The longest delay (exponential). |
| `jitter` | boolean |  | Randomizes the delays (default true). |

## `then`

<a id="then"></a>

fail (compensate, only before the pivot), manual (stop for an operator), complete, or { goto: step }.

One of:

- `fail` or `manual` or `complete`: fail compensates the completed steps (only before the pivot, WF-V07); manual stops the instance for an operator; complete ends it as completed.
- an object with `goto`: Continue at another step (WF-V11).

  | Key | Type | Required | Description |
  |-----|------|----------|-------------|
  | `goto` | [kebab](#kebab) | yes | The step to continue with. |

## `compensate`

<a id="compensate"></a>

How to undo a step: a command and the event that confirms it, with a retry and a timeout; or none with a reason.

One of:

- `none`: WF-V09 asks for the object form with a reason.
- an object with `send`, `await`, `retry`, `timeout`, `none`, `reason`

  | Key | Type | Required | Description |
  |-----|------|----------|-------------|
  | `send` | [send](#send) |  | The undoing command. |
  | `await` | object: `success` |  | The event that confirms the undo. |
  | `await.success` | [success](#success) | yes | The event that confirms the undo. |
  | `retry` | [retry](#retry) |  | Sends the undoing command again after a rejection or a timeout; when it gives up, the instance stops for an operator. |
  | `timeout` | [timeout](#timeout) |  | How long each undo attempt waits. |
  | `none` | boolean |  | The step has nothing to undo; give the reason. |
  | `reason` | string |  | Why the step needs no compensation. |

## `on`

<a id="on"></a>

What happens when an instance ends.

| Key | Type | Required | Description |
|-----|------|----------|-------------|
| `completed` | object: `send` |  | Sent when the instance completes. |
| `completed.send` | [send](#send) |  | The command to send. |
| `compensated` | object: `send` |  | Sent when the instance was compensated; $failure.code and $failure.message say why. |
| `compensated.send` | [send](#send) |  | The command to send. |
| `manual` | object: `alert` |  | An instance stopped for an operator: alert: true counts it on the sourceflow.workflow.manual_intervention metric. |
| `manual.alert` | boolean |  | Counts the instance on the alert metric. |

## Value types

| Type | Form | Description |
|------|------|-------------|
| <a id="kebab"></a>`kebab` | string matching `^[a-z][a-z0-9]*(-[a-z0-9]+)*$` | Lower-case words of letters and digits joined by hyphens, for example order-fulfilment. |
| <a id="identifier"></a>`identifier` | string matching `^[A-Za-z_][A-Za-z0-9_]*$` | A field name: letters, digits and underscores, not starting with a digit. |
| <a id="messagetype"></a>`messageType` | string matching `^[a-z0-9]+([._-][a-z0-9]+)*\.v[1-9][0-9]*$` | A message type as {name}.v{version}, for example payments.charge.v1. |
| <a id="code"></a>`code` | string matching `^[a-z0-9]+([._-][a-z0-9]+)*$` | A rejection error code. |
| <a id="duration"></a>`duration` | string matching `^P(?=\d\\|T\d)(\d+W)?(\d+D)?(T(?=\d)(\d+H)?(\d+M)?(\d+(\.\d+)?S)?)?$` | An ISO-8601 duration without years and months: PT30S, PT5M, PT1H, P1D, P1DT12H. |
| <a id="guard"></a>`guard` | string | A CESQL boolean expression over $data.*, event.data.* (doc 15 §5.2). |
